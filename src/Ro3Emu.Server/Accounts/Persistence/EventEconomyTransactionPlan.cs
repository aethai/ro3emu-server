using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Value-only event economy command. Assets, source, result, revisions and accounting share BEGIN/COMMIT.</summary>
internal sealed class EventEconomyTransactionPlan(RoleItemPlan child, EventEconomyMutation semanticMutation,
    bool failAcknowledgementForTest = false) : DbPlan
{
    private readonly EventEconomyMutation mutation = semanticMutation.BindPlan(EventPlanFingerprint.Capture(child));
    private bool replay;
    private int acknowledgementFault = failAcknowledgementForTest ? 1 : 0;
    internal override string OperationId => mutation.OperationId;
    internal override bool IsReplay => replay;
    internal override int Bytes => checked(child.Bytes * 2 + mutation.IntentPayload.Length + mutation.Outcome.Length + 8192);
    internal override string Label => "economy/" + mutation.CommandKind;
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => child.RoleWrites;
    internal override IReadOnlyList<long> RolePins => child.RolePins;
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => child.EntityWrites;
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites => child.EntityDeltaWrites;
    internal override IReadOnlyList<RoleRevisionKey> MutatedRoleWrites(object? result) => child.MutatedRoleWrites(result);
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityWrites(object? result) => child.MutatedEntityWrites(result);
    internal override IReadOnlyList<OwnerRevisionKey> MutatedEntityDeltaWrites(object? result) => child.MutatedEntityDeltaWrites(result);
    internal override void ResolveRoleWrites(AccountStore accounts) => child.ResolveRoleWrites(accounts);
    internal override void AfterCommit()
    {
        if (Interlocked.Exchange(ref acknowledgementFault, 0) != 0)
            throw new IOException("Test-only lost event commit acknowledgement.");
    }

    internal override bool TryReplay(SqliteConnection db, SqliteTransaction tx, out object? result)
    {
        using var query = DbSql.Command(db, tx,
            """
            SELECT intent_hash,intent_payload,child_receipts,realm_id,actor_account,actor_role,command_kind,
                request_fingerprint,source_key,outcome,leg_count,effective_utc_ms
            FROM operation_receipt WHERE operation_id=$op
            """);
        query.Parameters.AddWithValue("$op", mutation.OperationId);
        using var row = query.ExecuteReader();
        if (row.Read())
        {
            if (!ReceiptBlobs.SameIntent(row.GetString(0), (byte[])row.GetValue(1), mutation.IntentHash,
                    mutation.IntentPayload, mutation.LegacyIntent) ||
                row.GetString(4) != mutation.ActorAccount || row.GetInt64(5) != mutation.ActorRoleId ||
                row.GetString(6) != mutation.CommandKind || row.GetString(7) != mutation.RequestFingerprint ||
                row.GetString(8) != mutation.SourceKey || !ReceiptBlobs.SameOutcome((byte[])row.GetValue(9), (byte[])row.GetValue(1), mutation.Outcome) ||
                row.GetInt64(11) != mutation.EffectiveUtcMs)
                throw new EconomyOperationConflictException("Event operation ID belongs to another frozen intent.");
            result = EconomyTransactionPlan.DecodeChildReceipts((byte[])row.GetValue(2));
            if (((object?[])result).Length != child.ChildCount) throw new InvalidDataException("Invalid event child receipt count.");
            string realm = row.GetString(3);
            long legCount = row.GetInt64(10);
            row.Close();
            using var meta = DbSql.Command(db, tx, "SELECT realm_id FROM economy_meta WHERE singleton=1");
            if (meta.ExecuteScalar() is not string currentRealm || currentRealm != realm)
                throw new InvalidDataException("Event receipt has a foreign realm identity.");
            using var legs = DbSql.Command(db, tx, "SELECT COUNT(*) FROM economy_entry WHERE operation_id=$op");
            legs.Parameters.AddWithValue("$op", mutation.OperationId);
            if (legCount <= 0 || (long)legs.ExecuteScalar()! != legCount)
                throw new InvalidDataException("Event receipt has an invalid ledger leg count.");
            using var source = DbSql.Command(db, tx,
                "SELECT 1 FROM operation_source WHERE realm_id=$realm AND source_key=$source AND operation_id=$op");
            source.Parameters.AddWithValue("$realm", realm);
            source.Parameters.AddWithValue("$source", mutation.SourceKey);
            source.Parameters.AddWithValue("$op", mutation.OperationId);
            if (source.ExecuteScalar() is null) throw new InvalidDataException("Event receipt has no source claim.");
            replay = true;
            return true;
        }
        row.Close();
        using var used = DbSql.Command(db, tx, "SELECT operation_id FROM operation_source WHERE source_key=$source");
        used.Parameters.AddWithValue("$source", mutation.SourceKey);
        if (used.ExecuteScalar() is string prior)
            throw new EconomyOperationConflictException("Natural event source already belongs to operation " + prior);
        if (OperationReceipts.TryRead(db, tx, mutation.OperationId, out _))
            throw new InvalidDataException("Event operation has an owner receipt without its economic receipt.");
        result = null;
        return false;
    }

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (mutation.ActorRoleId > 0)
        {
            using var actor = DbSql.Command(db, tx, "SELECT account FROM role WHERE id=$role");
            actor.Parameters.AddWithValue("$role", mutation.ActorRoleId);
            if (actor.ExecuteScalar() as string != mutation.ActorAccount || !RolePins.Contains(mutation.ActorRoleId))
                throw new EconomyOperationConflictException("Event actor is not a captured role of this account.");
        }
        var entities = EntityWrites.Concat(EntityDeltaWrites).Distinct().ToArray();
        var entityBefore = entities.Select(key => (OwnerRevisionValue)new OwnerRevisionReadPlan(key).Execute(db, tx)).ToArray();
        var before = EconomySnapshotReader.ReadEventHoldings(db, tx, mutation.Catalog, RoleWrites, entities);
        var result = (object?[])child.Execute(db, tx);
        var after = EconomySnapshotReader.ReadEventHoldings(db, tx, mutation.Catalog, RoleWrites, entities);
        var legs = BuildLegs(before, after, mutation);
        if (legs.Count == 0) throw new InvalidOperationException("Event economy envelope must explain an economic movement.");
        var mutated = MutatedRoleWrites(result).ToHashSet();
        var beforeVector = RoleWrites.Distinct().Select(key => new
        {
            key.RoleId, key.Part,
            Epoch = ExpectedRevisions![key.RoleId].Epoch, Revision = ExpectedRevisions[key.RoleId].Revision(key.Part)
        }).ToArray();
        var afterVector = beforeVector.Select(value => new
        {
            value.RoleId, value.Part, value.Epoch,
            Revision = checked(value.Revision + (mutated.Contains(RevisionStore.Role(value.RoleId, value.Part)) ? 1 : 0))
        }).ToArray();
        var pins = RolePins.Distinct().Select(role => new { RoleId = role, ExpectedRevisions![role].Epoch }).ToArray();
        var changedEntities = MutatedEntityWrites(result).Concat(MutatedEntityDeltaWrites(result)).ToHashSet();
        var entityAfter = entityBefore.Select(value => new OwnerRevisionValue(value.Key,
            checked(value.Revision + (changedEntities.Contains(value.Key) ? 1 : 0)),
            value.Exists || changedEntities.Contains(value.Key))).ToArray();
        EconomyLedgerWrite.Append(db, tx, new EconomyLedgerCommit(
            mutation.OperationId, mutation.ActorAccount, mutation.ActorRoleId, mutation.CommandKind,
            mutation.RequestFingerprint, mutation.IntentHash, mutation.IntentPayload, mutation.SourceKey,
            mutation.EffectiveUtcMs, mutation.Outcome, EconomyTransactionPlan.EncodeChildReceipts(result),
            JsonSerializer.Serialize(new { Roles = beforeVector, Pins = pins, Entities = entityBefore }),
            JsonSerializer.Serialize(new { Roles = afterVector, Pins = pins, Entities = entityAfter }),
            legs, UseLegNumber: true));
        return result;
    }

    internal static List<EconomyLeg> BuildLegs(
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> before,
        Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> after, EventEconomyMutation intent)
    {
        var expected = intent.OwnerDeltas.GroupBy(q => (q.Owner, q.Asset))
            .ToDictionary(g => g.Key, g => g.Aggregate(0L, (n, q) => checked(n + q.Delta)));
        var keys = before.Keys.Concat(after.Keys).Distinct().ToArray();
        // Compare each beneficiary, including zero-total transfers between compartments.
        var actual = keys.GroupBy(k => (k.Location.Owner, k.Asset)).ToDictionary(g => g.Key,
            g => g.Aggregate(0L, (n, k) => checked(n + (after.GetValueOrDefault(k)?.Quantity ?? 0) - (before.GetValueOrDefault(k)?.Quantity ?? 0))));
        if (expected.Keys.Concat(actual.Keys).Distinct().Any(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k)))
            throw new InvalidOperationException("Event asset delta disagrees with the frozen beneficiaries.");
        var legs = new List<EconomyLeg>();
        int edge = 0;
        foreach (var asset in keys.Select(k => k.Asset).Concat(intent.Costs.Select(q => q.Asset)).Concat(intent.Goods.Select(q => q.Asset)).Distinct())
        {
            var changes = keys.Where(k => k.Asset == asset).Select(k => new Change(k.Location,
                before.GetValueOrDefault(k), after.GetValueOrDefault(k))).ToArray();
            if (asset.Kind != EconomyAssetKind.Item) foreach (var change in changes) change.CurrentFingerprint = null;
            long cost = intent.Costs.Where(q => q.Asset == asset).Aggregate(0L, (n, q) => checked(n + q.Quantity));
            long good = intent.Goods.Where(q => q.Asset == asset).Aggregate(0L, (n, q) => checked(n + q.Quantity));
            long negative = changes.Where(c => c.Remaining < 0).Aggregate(0L, (n, c) => checked(n - c.Remaining));
            long positive = changes.Where(c => c.Remaining > 0).Aggregate(0L, (n, c) => checked(n + c.Remaining));
            if (checked(positive - negative) != checked(good - cost)) throw new InvalidOperationException("Event faucet/sink mismatch.");
            long overlap = Math.Max(0, cost - negative);
            foreach (var change in changes.Where(c => c.Before != null))
            {
                if (overlap == 0) break;
                long amount = Math.Min(overlap, change.Before!.Quantity);
                Add(change, -amount, EconomyLegKind.Sink, intent.SinkReason, terminal: false);
                Add(change, amount, EconomyLegKind.Source, intent.SourceReason, terminal: false);
                overlap -= amount; cost -= amount; good -= amount;
            }
            if (overlap != 0) throw new InvalidOperationException("Event cost exceeds concrete opening holdings.");
            foreach (var change in changes.Where(c => c.Remaining < 0))
            {
                long amount = Math.Min(cost, -change.Remaining);
                if (amount == 0) continue;
                change.Remaining += amount; cost -= amount;
                Add(change, -amount, EconomyLegKind.Sink, intent.SinkReason, terminal: change.Remaining == 0);
            }
            foreach (var change in changes.Where(c => c.Remaining > 0))
            {
                long amount = Math.Min(good, change.Remaining);
                if (amount == 0) continue;
                change.Remaining -= amount; good -= amount;
                Add(change, amount, EconomyLegKind.Source, intent.SourceReason, terminal: change.Remaining == 0);
            }
            if (cost != 0 || good != 0) throw new InvalidOperationException("Unexplained event source or sink.");
            foreach (var debit in changes.Where(c => c.Remaining < 0))
                foreach (var credit in changes.Where(c => c.Remaining > 0))
                {
                    long amount = Math.Min(-debit.Remaining, credit.Remaining);
                    if (amount == 0) continue;
                    debit.Remaining += amount; credit.Remaining -= amount; edge++;
                    Add(debit, -amount, EconomyLegKind.Transfer, intent.TransferReason, debit.Remaining == 0, edge, credit.Location);
                    Add(credit, amount, EconomyLegKind.Transfer, intent.TransferReason, credit.Remaining == 0, edge, debit.Location);
                }
            if (changes.Any(c => c.Remaining != 0)) throw new InvalidOperationException("Unpaired event transfer.");
            foreach (var change in changes.Where(c => c.CurrentFingerprint != Fingerprint(c.After)))
                Add(change, 0, EconomyLegKind.Metadata, EconomyReason.ItemMetadata, terminal: true);

            string? Fingerprint(EconomyHolding? h) => asset.Kind == EconomyAssetKind.Item ? h?.Fingerprint : null;
            void Add(Change change, long delta, EconomyLegKind kind, EconomyReason reason, bool terminal,
                int edgeNumber = 0, EconomyLocation? counterparty = null)
            {
                string? prior = asset.Kind == EconomyAssetKind.Item ? change.CurrentFingerprint : null;
                string? next = asset.Kind == EconomyAssetKind.Item
                    ? terminal ? Fingerprint(change.After) : prior ?? Fingerprint(change.After) : null;
                legs.Add(new(0, intent.OperationId, legs.Count, kind, reason, asset, change.Location, delta,
                    edgeNumber, counterparty, prior, next, intent.SourceKey));
                change.CurrentFingerprint = next;
            }
        }
        return legs;
    }

    private sealed class Change(EconomyLocation location, EconomyHolding? before, EconomyHolding? after)
    {
        internal EconomyLocation Location = location;
        internal EconomyHolding? Before = before, After = after;
        internal long Remaining = checked((after?.Quantity ?? 0) - (before?.Quantity ?? 0));
        internal string? CurrentFingerprint = before?.Fingerprint;
    }
}
