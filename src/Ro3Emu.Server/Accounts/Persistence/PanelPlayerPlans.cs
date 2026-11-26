using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Several blobs of one role in one snapshot (the public profile's settings, title and achievements).</summary>
internal sealed class PanelRoleBlobsRead(long role, IReadOnlyList<string> columns) : DbReadPlan
{
    internal override int Bytes => 128 + columns.Count * 32;
    internal override string Label => "panel/role-blobs";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (string column in columns) RoleColumns.Validate(column);
        var blobs = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        using var cmd = DbSql.Command(db, tx, $"SELECT {string.Join(',', columns)} FROM role WHERE id=$id AND deleted_time=0");
        cmd.Parameters.AddWithValue("$id", role);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return blobs;
        for (int i = 0; i < columns.Count; i++) blobs[columns[i]] = row.IsDBNull(i) ? null : (byte[])row.GetValue(i);
        return blobs;
    }
}

/// <summary>One stack in a live character's bags, cart or equipment.</summary>
internal sealed record PanelItemRow(long Guid, long RoleId, long Entry, long Stack, int Pack, int Grid, string Name, string Account);

/// <summary>A live character's mailbox blob (JSON MailService.Mailbox).</summary>
internal sealed record PanelMailboxRow(long RoleId, string Name, string Account, byte[] Mailbox);

/// <summary>Everywhere an item can be held, raw: the item table, the mailboxes, the exchange's listings and the stalls
/// (market_record JSON) and the open auction lots (decoded by the caller with the services' own types).</summary>
internal sealed record PanelHoldingsRows(List<PanelItemRow> Items, List<PanelMailboxRow> Mailboxes, List<(string Kind, string Data)> Market,
    List<AuctionStore.Item> Auctions);

/// <summary>
/// The admin's item tracking: every place an item can be held, of one item (<paramref name="entry"/>) or of all. Only
/// characters that are not deleted count. Mailboxes are narrowed by the item id's digits in the blob before the caller
/// decodes them, so tracking one item does not decode every mailbox of the realm.
/// </summary>
internal sealed class PanelHoldingsRead(long? entry, IReadOnlyList<string> marketKinds) : DbReadPlan
{
    internal override int Bytes => 64 * 1024;
    internal override string Label => "panel/holdings";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx) =>
        new PanelHoldingsRows(Items(db, tx), Mailboxes(db, tx), Market(db, tx),
            (List<AuctionStore.Item>)new AuctionReadPlan(AuctionReadKind.Open).Execute(db, tx)!);

    private List<PanelItemRow> Items(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT i.guid,i.role_id,i.entry,i.stack,i.pack,i.grid,r.name,r.account FROM item i " +
            "JOIN role r ON r.id=i.role_id AND r.deleted_time=0 WHERE i.stack>0" + (entry != null ? " AND i.entry=$e" : "") + " ORDER BY i.guid");
        if (entry != null) cmd.Parameters.AddWithValue("$e", entry.Value);
        var rows = new List<PanelItemRow>();
        using var row = cmd.ExecuteReader();
        while (row.Read())
            rows.Add(new PanelItemRow(row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetInt32(4), row.GetInt32(5),
                row.GetString(6), row.GetString(7)));
        return rows;
    }

    private List<PanelMailboxRow> Mailboxes(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT id,name,account,mailbox FROM role WHERE deleted_time=0 AND mailbox IS NOT NULL" +
            (entry != null ? " AND instr(CAST(mailbox AS TEXT), $e) > 0" : ""));
        if (entry != null) cmd.Parameters.AddWithValue("$e", entry.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var rows = new List<PanelMailboxRow>();
        using var row = cmd.ExecuteReader();
        while (row.Read()) rows.Add(new PanelMailboxRow(row.GetInt64(0), row.GetString(1), row.GetString(2), (byte[])row.GetValue(3)));
        return rows;
    }

    private List<(string, string)> Market(SqliteConnection db, SqliteTransaction tx) =>
        [.. marketKinds.SelectMany(kind => MarketRecordReadPlan.Read(db, tx, kind, null).Select(r => (kind, r.Data)))];
}

/// <summary>One asset's ledger: the sum, count and time range of its legs of one kind (EconomyLegKind).</summary>
internal sealed record PanelLedgerRow(long Asset, int LegKind, long Sum, int Count, long FirstMs, long LastMs);

/// <summary>
/// The economy ledger summed by asset and leg kind, for items and currencies, of one asset or of all. Includes both
/// live <c>economy_entry</c> legs and pruned history rolled into <c>economy_daily_aggregate</c>. A database without the
/// ledger reads as empty.
/// </summary>
internal sealed class PanelLedgerTotalsRead(long? asset) : DbReadPlan
{
    internal override int Bytes => 4096;
    internal override string Label => "panel/ledger-totals";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var totals = new Dictionary<(long Asset, int LegKind), (long Sum, int Count, long FirstMs, long LastMs)>();
        using (var exists = DbSql.Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='economy_entry'"))
            if ((long)exists.ExecuteScalar()! == 0) return new List<PanelLedgerRow>();

        using (var cmd = DbSql.Command(db, tx, "SELECT asset_id,leg_kind,TOTAL(delta),COUNT(*),MIN(recorded_utc_ms),MAX(recorded_utc_ms) " +
            "FROM economy_entry WHERE asset_kind IN (1,2)" + (asset != null ? " AND asset_id=$a" : "") + " GROUP BY asset_id,leg_kind"))
        {
            if (asset != null) cmd.Parameters.AddWithValue("$a", asset.Value);
            using var row = cmd.ExecuteReader();
            while (row.Read())
            {
                var key = (row.GetInt64(0), row.GetInt32(1));
                totals[key] = (ClampLong(row.GetDouble(2)), row.GetInt32(3), row.GetInt64(4), row.GetInt64(5));
            }
        }

        MergeDailyAggregates(db, tx, totals);

        var rows = new List<PanelLedgerRow>(totals.Count);
        foreach (var ((assetId, legKind), value) in totals)
            rows.Add(new PanelLedgerRow(assetId, legKind, value.Sum, value.Count, value.FirstMs, value.LastMs));
        return rows;
    }

    private void MergeDailyAggregates(SqliteConnection db, SqliteTransaction tx,
        Dictionary<(long Asset, int LegKind), (long Sum, int Count, long FirstMs, long LastMs)> totals)
    {
        using (var exists = DbSql.Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='economy_daily_aggregate'"))
            if ((long)exists.ExecuteScalar()! == 0) return;

        using var cmd = DbSql.Command(db, tx, "SELECT asset_id,leg_kind,TOTAL(net_delta),TOTAL(entry_count),MIN(day_utc),MAX(day_utc) " +
            "FROM economy_daily_aggregate WHERE asset_kind IN (1,2)" + (asset != null ? " AND asset_id=$a" : "") + " GROUP BY asset_id,leg_kind");
        if (asset != null) cmd.Parameters.AddWithValue("$a", asset.Value);
        using var row = cmd.ExecuteReader();
        while (row.Read())
        {
            var key = (row.GetInt64(0), row.GetInt32(1));
            double aggSum = row.GetDouble(2);
            long aggCount = (long)Math.Clamp(Math.Round(row.GetDouble(3)), 0, int.MaxValue);
            long aggFirst = ParseDayUtcMs(row.IsDBNull(4) ? null : row.GetString(4), endOfDay: false);
            long aggLast = ParseDayUtcMs(row.IsDBNull(5) ? null : row.GetString(5), endOfDay: true);
            if (totals.TryGetValue(key, out var existing))
            {
                long first = existing.FirstMs > 0 && aggFirst > 0 ? Math.Min(existing.FirstMs, aggFirst) : Math.Max(existing.FirstMs, aggFirst);
                long last = Math.Max(existing.LastMs, aggLast);
                int count = (int)Math.Min((long)existing.Count + aggCount, int.MaxValue);
                totals[key] = (ClampLong((double)existing.Sum + aggSum), count, first, last);
            }
            else
            {
                totals[key] = (ClampLong(aggSum), (int)aggCount, aggFirst, aggLast);
            }
        }
    }

    private static long ClampLong(double sum) =>
        sum >= long.MaxValue ? long.MaxValue : sum <= long.MinValue ? long.MinValue : (long)Math.Round(sum);

    private static long ParseDayUtcMs(string? dayUtc, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(dayUtc)
            || !DateTimeOffset.TryParseExact(dayUtc, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            return 0;
        return endOfDay ? dt.AddDays(1).AddMilliseconds(-1).ToUnixTimeMilliseconds() : dt.ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// Characters (deleted ones too: their kit entered the realm) that have their starter kit but no ledger receipt of
/// the kind <paramref name="kitKind"/>: they got it before the kit was written to the ledger. Without the ledger
/// tables every character with a kit counts.
/// </summary>
internal sealed class PanelUnrecordedStarterKitsRead(string kitKind) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "panel/unrecorded-starter-kits";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        bool ledger;
        using (var exists = DbSql.Command(db, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='operation_receipt'"))
            ledger = (long)exists.ExecuteScalar()! > 0;
        // One pass over the receipts (an uncorrelated IN), not one per character.
        using var count = DbSql.Command(db, tx, "SELECT COUNT(*) FROM role WHERE starter_kit<>0" + (ledger
            ? " AND id NOT IN (SELECT actor_role FROM operation_receipt WHERE command_kind=$kind)" : ""));
        count.Parameters.AddWithValue("$kind", kitKind);
        return (long)count.ExecuteScalar()!;
    }
}
