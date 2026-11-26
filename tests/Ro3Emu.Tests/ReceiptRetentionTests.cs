using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>
/// Receipt growth (issue from a host: 22 MB to 497 MB in two days): intent v2 no longer embeds the outcome, and
/// ReceiptRetention compacts old receipts without breaking replay, idempotency or the ledger audit.
/// </summary>
public sealed class ReceiptRetentionTests : IDisposable
{
    private readonly string _directory;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly long _role;
    private readonly ServerOptions _options;
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    { [1] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item });

    public ReceiptRetentionTests()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _directory = Path.Combine(root, ".loadtest-temp", "receipt-retention-" + Guid.NewGuid().ToString("N"));
        _options = new ServerOptions { DataDir = _directory };
        _options.ReceiptRetention.Days = 1;
        _options.ReceiptRetention.BatchSize = 2;
        _options.ReceiptRetention.LedgerDays = 0;
        _options.ReceiptRetention.MailDays = 0;
        _accounts = new AccountStore(Options.Create(_options));
        _roles = new RoleStore(_accounts);
        _role = _roles.Create("retention-account", 1, "Ret" + Guid.NewGuid().ToString("N")[..8], 1, 1000, 1)!.Id;
    }

    public void Dispose()
    {
        _accounts.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private EconomyMutation Kill(string operation, long exp) => new(operation, "retention-account", _role, "monster-kill",
        "kill/roll/empty", "monster:" + operation, 1_000_000, [], [], Encoding.UTF8.GetBytes("{\"drops\":[],\"exp\":" + exp + "}"),
        new EconomyRewardEvidence(1, exp - 10, 1, exp, 1, 0, 1, 0, 10, 0));

    private RoleItemPlanBuilder KillTransaction(EconomyMutation mutation, long exp)
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(_role, [], []);
        var progress = _roles.LoadProgress(_role);
        progress.Exp = exp;
        tx.Progress(_role, progress);
        tx.WithEconomy(mutation);
        return tx;
    }

    private string Commit(long exp)
    {
        string operation = Guid.NewGuid().ToString("N");
        _roles.Commit(KillTransaction(Kill(operation, exp), exp), "test/retention-kill");
        return operation;
    }

    private void Run(string sql, params (string Name, object? Value)[] values) => FixtureSql.Execute(_accounts, sql, values);

    private (byte[] Payload, byte[] Outcome) Blobs(string operation)
    {
        using var db = _accounts.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT intent_payload,outcome FROM operation_receipt WHERE operation_id=$op";
        command.Parameters.AddWithValue("$op", operation);
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        return ((byte[])row.GetValue(0), (byte[])row.GetValue(1));
    }

    private ReceiptRetention Retention() => new(Options.Create(_options), _accounts, TimeProvider.System,
        NullLogger<ReceiptRetention>.Instance);

    [Fact]
    public void IntentV2BindsTheOutcomeByHashInsteadOfEmbeddingIt()
    {
        var mutation = Kill("v2-intent", 10);
        string json = Encoding.UTF8.GetString(mutation.IntentPayload);
        Assert.Contains("\"OutcomeHash\"", json);
        Assert.DoesNotContain("\"Outcome\"", json);
        var legacy = mutation.LegacyIntent();
        Assert.Contains("\"Outcome\"", Encoding.UTF8.GetString(legacy.Payload));
        Assert.NotEqual(mutation.IntentHash, legacy.Hash);
    }

    [Fact]
    public async Task ACompactedKillStillReplaysAndTheLedgerCertifiesItsSequenceLegsAndSource()
    {
        string operation = Guid.NewGuid().ToString("N");
        var mutation = Kill(operation, 10);
        await _roles.CommitAsync(KillTransaction(mutation, 10), "test/retention-first");
        string recent = Commit(20);
        Run("UPDATE operation_receipt SET recorded_utc_ms=1 WHERE operation_id=$op", ("$op", operation));
        long recentOwners = Scalar<long>("SELECT COUNT(*) FROM owner_operation_receipt WHERE operation_id=$op", recent);

        Assert.Equal(1, await Retention().CompactAsync());
        var (payload, outcome) = Blobs(operation);
        Assert.True(ReceiptBlobs.IsCompacted(payload));
        Assert.Empty(outcome);
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(recent).Payload)); // recent receipts keep their bytes

        // Its child results and owner acknowledgement are gone too (#473: about 4 KB a kill, kept for good before).
        Assert.True(ReceiptBlobs.IsCompacted(Scalar<byte[]>("SELECT child_receipts FROM operation_receipt WHERE operation_id=$op", operation)));
        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM owner_operation_receipt WHERE operation_id=$op", operation));
        Assert.Equal(recentOwners, Scalar<long>("SELECT COUNT(*) FROM owner_operation_receipt WHERE operation_id=$op", recent));

        // The same operation again, past the window, is refused as a conflict: never a second grant.
        await Assert.ThrowsAnyAsync<EconomyOperationConflictException>(() =>
            _roles.CommitAsync(KillTransaction(Kill(operation, 10), 10), "test/retention-replay"));
        Assert.Equal(20, _roles.LoadProgress(_role).Exp);

        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        Assert.Empty(ledger.Issues);
        Assert.Equal(1, ledger.CompactedReceipts);
        Assert.Equal(0, await Retention().CompactAsync()); // nothing left to do
    }

    [Fact]
    public async Task AReceiptWrittenWithTheV1IntentStillReplaysAndVerifies()
    {
        string operation = Guid.NewGuid().ToString("N");
        var mutation = Kill(operation, 10);
        await _roles.CommitAsync(KillTransaction(mutation, 10), "test/retention-v1");
        var (hash, payload) = mutation.LegacyIntent();
        Run("UPDATE operation_receipt SET intent_hash=$hash,intent_payload=$payload WHERE operation_id=$op",
            ("$hash", hash), ("$payload", payload), ("$op", operation));

        await _roles.CommitAsync(KillTransaction(Kill(operation, 10), 10), "test/retention-v1-replay");
        Assert.Equal(10, _roles.LoadProgress(_role).Exp);
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.Empty(ledger.Issues);
        Assert.Equal(0, ledger.CompactedReceipts);
    }

    [Fact]
    public async Task MailDeliveriesAreNeverCompactedAndRetentionCanBeOff()
    {
        string mail = Commit(10), kill = Commit(20);
        Run("UPDATE operation_receipt SET recorded_utc_ms=1");
        // MailDeliveryReceiptReadPlan re-reads a delivery's payload whenever its delivery key comes back.
        Run("UPDATE operation_receipt SET command_kind='mail/deliver' WHERE operation_id=$op", ("$op", mail));

        _options.ReceiptRetention.Days = 0;
        Assert.Equal(0, await Retention().CompactAsync());
        _options.ReceiptRetention.Days = 1;
        Assert.Equal(1, await Retention().CompactAsync());
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(mail).Payload));
        Assert.True(ReceiptBlobs.IsCompacted(Blobs(kill).Payload));
    }

    private T Scalar<T>(string sql, string operation)
    {
        using var db = _accounts.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$op", operation);
        return (T)command.ExecuteScalar()!;
    }

    /// <summary>A database compacted by an earlier version: payloads dropped up to the watermark, child results and owner
    /// acknowledgements kept. The next run drops those once (compacted_full_seq), then compacts new receipts as usual.</summary>
    [Fact]
    public async Task ReceiptsCompactedByAnEarlierVersionLoseTheirChildResultsOnce()
    {
        string[] old = [Commit(10), Commit(20), Commit(30)];
        string mail = Commit(40);
        Run("UPDATE operation_receipt SET command_kind='mail/deliver' WHERE operation_id=$op", ("$op", mail));
        Run("UPDATE operation_receipt SET intent_payload=$marker,outcome=X'' WHERE command_kind NOT LIKE 'mail/%'",
            ("$marker", ReceiptBlobs.CompactedPayload));
        Run("UPDATE economy_meta SET compacted_seq=(SELECT MAX(commit_seq) FROM operation_receipt),compacted_full_seq=0");
        string fresh = Commit(50);
        Run("UPDATE operation_receipt SET recorded_utc_ms=1 WHERE operation_id=$op", ("$op", fresh));

        Assert.Equal(1, await Retention().CompactAsync()); // the backfill (two batches of 2), then the new receipt
        foreach (string op in old.Append(fresh))
        {
            Assert.True(ReceiptBlobs.IsCompacted(Scalar<byte[]>("SELECT child_receipts FROM operation_receipt WHERE operation_id=$op", op)));
            Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM owner_operation_receipt WHERE operation_id=$op", op));
        }
        Assert.False(ReceiptBlobs.IsCompacted(Scalar<byte[]>("SELECT child_receipts FROM operation_receipt WHERE operation_id=$op", mail)));
        Assert.Equal(LastSequence(), Watermark());
        Assert.Equal(Watermark(), Scalar<long>("SELECT compacted_full_seq FROM economy_meta WHERE singleton=1 AND $op=$op", ""));
        Assert.Equal(0, await Retention().CompactAsync());
    }

    private long Watermark()
    {
        using var db = _accounts.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT compacted_seq FROM economy_meta WHERE singleton=1";
        return (long)command.ExecuteScalar()!;
    }

    private long LastSequence()
    {
        using var db = _accounts.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT MAX(commit_seq) FROM operation_receipt";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task ASecondRunWalksNothingAndTheNextOneResumesFromTheWatermark()
    {
        string[] old = [Commit(10), Commit(20), Commit(30)];
        Run("UPDATE operation_receipt SET recorded_utc_ms=1");
        var retention = Retention();

        Assert.Equal(3, await retention.CompactAsync()); // two batches of BatchSize 2
        Assert.Equal(3, retention.LastPassed);
        Assert.Equal(LastSequence(), Watermark());
        Assert.All(old, op => Assert.True(ReceiptBlobs.IsCompacted(Blobs(op).Payload)));

        // Right after: nothing behind the watermark is read again.
        Assert.Equal(0, await retention.CompactAsync());
        Assert.Equal(0, retention.LastPassed);

        // A receipt not yet due holds the watermark; once due, the next run walks only it.
        string fresh = Commit(40);
        Assert.Equal(0, await retention.CompactAsync());
        Assert.Equal(0, retention.LastPassed);
        Run("UPDATE operation_receipt SET recorded_utc_ms=1 WHERE operation_id=$op", ("$op", fresh));
        Assert.Equal(1, await retention.CompactAsync());
        Assert.Equal(1, retention.LastPassed);
        Assert.Equal(LastSequence(), Watermark());
    }

    /// <summary>A host that booted with a wrong clock: one receipt dated 2100 must not stop compaction for 74 years.</summary>
    [Fact]
    public async Task AReceiptDatedFarInTheFutureDoesNotStopCompactionAndKeepsItsData()
    {
        string misdated = Commit(10);
        string[] old = [Commit(20), Commit(30)];
        Run("UPDATE operation_receipt SET recorded_utc_ms=1");
        Run("UPDATE operation_receipt SET recorded_utc_ms=$at WHERE operation_id=$op",
            ("$at", new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()), ("$op", misdated));
        var retention = Retention();

        Assert.Equal(2, await retention.CompactAsync());
        Assert.Equal(3, retention.LastPassed);
        Assert.Equal(LastSequence(), Watermark());
        Assert.All(old, op => Assert.True(ReceiptBlobs.IsCompacted(Blobs(op).Payload)));
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(misdated).Payload));
    }

    /// <summary>A receipt only a little ahead of the clock is a short clock step: it waits, as a young receipt does.</summary>
    [Fact]
    public async Task AReceiptWithinADayAheadOfTheClockStillHoldsTheWatermark()
    {
        string ahead = Commit(10);
        long before = Watermark();
        Run("UPDATE operation_receipt SET recorded_utc_ms=$at WHERE operation_id=$op",
            ("$at", DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeMilliseconds()), ("$op", ahead));
        var retention = Retention();

        Assert.Equal(0, await retention.CompactAsync());
        Assert.Equal(before, Watermark());
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(ahead).Payload));
    }

    [Fact]
    public void BlobComparisonAcceptsTheCurrentLegacyOrCompactedIntentOnly()
    {
        byte[] current = "{\"v\":2}"u8.ToArray(), legacy = "{\"v\":1}"u8.ToArray(), other = "{\"v\":3}"u8.ToArray();
        (string, byte[]) Legacy() => ("LEGACY", legacy);
        Assert.True(ReceiptBlobs.SameIntent("NEW", current, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameIntent("LEGACY", legacy, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameIntent("NEW", ReceiptBlobs.CompactedPayload, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameIntent("LEGACY", ReceiptBlobs.CompactedPayload, "NEW", current, Legacy));
        Assert.False(ReceiptBlobs.SameIntent("NEW", other, "NEW", current, Legacy));
        Assert.False(ReceiptBlobs.SameIntent("OTHER", ReceiptBlobs.CompactedPayload, "NEW", current, Legacy));
        Assert.True(ReceiptBlobs.SameOutcome([], ReceiptBlobs.CompactedPayload, [1, 2]));
        Assert.False(ReceiptBlobs.SameOutcome([], current, [1, 2]));
        Assert.True(ReceiptBlobs.SameOutcome([1, 2], current, [1, 2]));
    }

    [Fact]
    public async Task OldLedgerEntriesRollIntoDailyAggregateAndPruneCleanly()
    {
        var progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 100;
        _roles.SaveProgress(_role, progress);

        string op = Guid.NewGuid().ToString("N");
        var buy = _roles.NewTransaction();
        buy.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 2, Pack = 1, Sort = 1 }]);
        progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 90;
        buy.Progress(_role, progress);
        buy.RoleBlob(_role, "shop", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Ro3Emu.Server.Game.ShopService.ShopState()));
        buy.WithEconomy(new EconomyMutation(op, "retention-account", _role, "npc-shop-buy", "agg-buy",
            "npc:" + op, 1_000_000, [new(new(EconomyAssetKind.Currency, 1), 10)],
            [new(new(EconomyAssetKind.Item, 3), 2)], [1]));
        _roles.Commit(buy, "test/retention-ledger-prune");

        long oldTs = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Run("UPDATE operation_receipt SET recorded_utc_ms=$ts WHERE operation_id=$op", ("$ts", oldTs), ("$op", op));
        Run("UPDATE economy_entry SET recorded_utc_ms=$ts WHERE operation_id=$op", ("$ts", oldTs), ("$op", op));

        _options.ReceiptRetention.LedgerDays = 30;
        var retention = Retention();
        await retention.CompactAsync();
        Assert.Equal(2, retention.LastLedgerEntriesPruned);

        Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM economy_entry WHERE operation_id=$op", op));
        using (var db = _accounts.Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), SUM(entry_count), SUM(abs_delta) FROM economy_daily_aggregate WHERE day_utc='2025-01-15'";
            using var row = cmd.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(2L, row.GetInt64(0));
            Assert.Equal(2L, row.GetInt64(1));
            Assert.Equal(12L, row.GetInt64(2)); // 10 currency + 2 items
        }
        var ledger = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 1);
        Assert.Contains(ledger.Issues, i => i.Key == "ledger/pruned" && i.Kind == EconomyIssueKind.Coverage);
        Assert.DoesNotContain(ledger.Issues, i => i.Kind == EconomyIssueKind.Corruption);

        // A second unpruned receipt committed after pruning must verify cleanly with zero issues from pruned_entry_seq.
        string op2 = Guid.NewGuid().ToString("N");
        var buy2 = _roles.NewTransaction();
        buy2.ItemDelta(_role, [], [new InvItem { Entry = 3, Stack = 1, Pack = 1, Sort = 1 }]);
        progress = _roles.LoadProgress(_role);
        progress.Currency[1] = 85;
        buy2.Progress(_role, progress);
        buy2.RoleBlob(_role, "shop", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Ro3Emu.Server.Game.ShopService.ShopState()));
        buy2.WithEconomy(new EconomyMutation(op2, "retention-account", _role, "npc-shop-buy", "agg-buy-2",
            "npc:" + op2, 2_000_000, [new(new(EconomyAssetKind.Currency, 1), 5)],
            [new(new(EconomyAssetKind.Item, 3), 1)], [1]));
        _roles.Commit(buy2, "test/retention-ledger-after-prune");

        var fullWindow = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 0, 2);
        Assert.DoesNotContain(fullWindow.Issues, i => i.Kind == EconomyIssueKind.Corruption);
        Assert.Single(fullWindow.Issues, i => i.Key == "ledger/pruned");

        var unprunedWindow = EconomyLedgerReader.Read(_accounts.DatabasePath, Catalog, "auto", 1, 2);
        Assert.Empty(unprunedWindow.Issues);
        Assert.True(unprunedWindow.CompleteHistory);

        // Replaying leg validation on a pruned operation succeeds instead of throwing InvalidDataException.
        using (var db = _accounts.Open())
        using (var tx = db.BeginTransaction(deferred: true))
        {
            FamilyEconomyTransactionPlan.ValidateLegCount(db, tx, op, 2);
            FamilyEconomyTransactionPlan.ValidateLegCount(db, tx, op2, 2);
        }

        // Panel economy summary includes economy_daily_aggregate totals for pruned history when unfiltered by role/account.
        var (liveCount, sumIn, sumOut, _) = await _accounts.ReadPanelAsync<(int, long, long, List<PanelEconomyRow>)>(
            new PanelEconomyRead(new PanelEconomyFilter("", "", [1], null, null), 0, 10));
        Assert.Equal(1, liveCount);
        Assert.Equal(0L, sumIn);
        Assert.Equal(-15L, sumOut); // -10 pruned (daily aggregate) + -5 live

        var (_, itemIn, itemOut, _) = await _accounts.ReadPanelAsync<(int, long, long, List<PanelEconomyRow>)>(
            new PanelEconomyRead(new PanelEconomyFilter("", "", [3], null, null), 0, 10));
        Assert.Equal(3L, itemIn); // +2 pruned (daily aggregate) + +1 live
        Assert.Equal(0L, itemOut);

        // Date filter covering only the pruned day still reports its aggregated totals.
        long dayStart = new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long dayEnd = new DateTimeOffset(2025, 1, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var (oldLive, oldIn, oldOut, _) = await _accounts.ReadPanelAsync<(int, long, long, List<PanelEconomyRow>)>(
            new PanelEconomyRead(new PanelEconomyFilter("", "", [1], dayStart, dayEnd), 0, 10));
        Assert.Equal(0, oldLive);
        Assert.Equal(0L, oldIn);
        Assert.Equal(-10L, oldOut);
    }

    [Fact]
    public void RetentionAndBackupDefaultsStaySafeAndMailScanUsesPartialIndex()
    {
        var defaults = new ServerOptions();
        Assert.Equal(0, defaults.ReceiptRetention.LedgerDays);
        Assert.Equal(0, defaults.ReceiptRetention.MailDays);
        Assert.Equal(0, defaults.Backup.MaxTotalMegabytes);
        Assert.True(defaults.StartupIntegrityCheck);
        Assert.False(defaults.StartupFullIntegrityCheck);

        using var db = _accounts.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT operation_id, commit_seq, recorded_utc_ms, source_key, intent_payload
            FROM operation_receipt
            WHERE commit_seq > 0 AND command_kind >= 'mail/' AND command_kind < 'mail0'
            ORDER BY commit_seq LIMIT 100
            """;
        var plan = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("operation_receipt_mail_seq", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OldMailReceiptsCompactIntoMailDeliveryReplayAndStillReplay()
    {
        string op = Guid.NewGuid().ToString("N");
        string source = "mail:delivery:retention-test";
        string deliveryKey = "k-retention-1";
        string semanticHash = "HASH-ABC-123";
        string letterId = "987654321";
        string evidenceJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Role = _role, DeliveryKey = deliveryKey, DeliverySemanticHash = semanticHash, LetterId = letterId,
        });
        byte[] evidenceBytes = Encoding.UTF8.GetBytes(evidenceJson);
        string evidenceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(evidenceBytes));
        byte[] payload = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
        {
            OperationId = op, SourceKey = source, CommandKind = "mail/deliver", ActorAccount = "", ActorRoleId = 0L,
            Evidence = new[] { new { Json = evidenceJson, Bytes = evidenceBytes.Length, Hash = evidenceHash } },
        }));
        string payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));

        long fiveDaysAgo = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds();
        string realm = Scalar<string>("SELECT realm_id FROM economy_meta WHERE singleton=1 AND $op=$op", "");
        Run("""
            UPDATE economy_meta SET next_sequence=next_sequence+1 WHERE singleton=1;
            INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind,
                request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,
                child_receipts,before_revisions,after_revisions,leg_count)
            VALUES($op,$realm,(SELECT next_sequence FROM economy_meta WHERE singleton=1),'',0,'mail/deliver',
                $hash,$hash,$payload,$source,$ts,$ts,X'',X'','','',0);
            INSERT INTO operation_source(realm_id,source_key,operation_id) VALUES($realm,$source,$op);
            """, ("$op", op), ("$realm", realm), ("$hash", payloadHash), ("$payload", payload), ("$source", source), ("$ts", fiveDaysAgo));

        _options.ReceiptRetention.MailDays = 30;
        var retention = Retention();
        await retention.CompactAsync();
        Assert.Equal(0, retention.LastMailCompacted);
        Assert.False(ReceiptBlobs.IsCompacted(Blobs(op).Payload));

        long fortyDaysAgo = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeMilliseconds();
        Run("UPDATE operation_receipt SET recorded_utc_ms=$ts WHERE operation_id=$op", ("$ts", fortyDaysAgo), ("$op", op));

        await retention.CompactAsync();
        Assert.Equal(1, retention.LastMailCompacted);
        Assert.True(ReceiptBlobs.IsCompacted(Blobs(op).Payload));

        string? replayed = await _accounts.ReadAsync<string?>(
            new MailDeliveryReceiptReadPlan(source, _role, deliveryKey, semanticHash));
        Assert.Equal(letterId, replayed);
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            _accounts.ReadAsync<string?>(new MailDeliveryReceiptReadPlan(source, _role, deliveryKey, "WRONG-HASH")));
    }

    [Fact]
    public void EveryPersistedTableHasAnExplicitRetentionPolicy()
    {
        var policies = new HashSet<string>(StringComparer.Ordinal)
        {
            // Bounded account, auth, and panel tables
            "account", "auth_token", "account_ban", "admin_audit", "panel_news", "panel_role",
            "account_login", "account_totp", "feedback_triage", "feedback_note", "panel_setting",
            // Anti-cheat hourly tables (pruned by AntiCheatFlushPlan)
            "anticheat_refusal", "anticheat_exp",
            // Revision and receipt tables (compacted/pruned by ReceiptRetention)
            "role_lifecycle", "owner_revision", "owner_operation_receipt",
            "economy_meta", "operation_receipt", "operation_source", "economy_entry",
            "economy_daily_aggregate", "mail_delivery_replay",
            // Per-role and per-entity gameplay state
            "role", "item", "server_state", "tower_first_pass",
            "friend", "friend_apply", "blacklist", "friend_metadata", "friend_chat_group", "friend_chat_member",
            "recent_team_peer", "adventure_group", "adventure_group_member",
            "market_record", "trade_listing", "auction_item", "auction_run", "auction_bonus", "auction_source", "auction_prebuy",
            "guild", "guild_member", "guild_market", "guild_recovery", "guild_event",
            "guild_caravan_member", "guild_caravan_version", "guild_campfire", "guild_hunting",
            "guild_challenge", "guild_challenge_clear",
            "territory_wars", "territory_wars_seasons", "territory_escort", "guild_league",
            "festival_round", "festival_reward",
            "dungeon_reward_obligation", "mvp_award_obligation", "mvp_settlement",
            "timed_battle_obligation", "casual_battle_obligation",
            "gift_code", "gift_code_redemption",
            "activity_pre_progress", "activity_pre_donation_area", "activity_eve_global",
            "mvp_state", "mvp_line_cooldown", "mvp_player",
            "prontera_defense_world", "prontera_defense_station", "prontera_defense_guild_prep",
        };
        using var db = _accounts.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var unclassified = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string table = reader.GetString(0);
            if (!policies.Contains(table)) unclassified.Add(table);
        }
        Assert.Empty(unclassified);
    }
}
