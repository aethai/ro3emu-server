using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Bounded retention batch for <c>mail/*</c> receipt compaction (extracting replay metadata into
/// <c>mail_delivery_replay</c>) and <c>economy_entry</c> pruning (rolling old legs into
/// <c>economy_daily_aggregate</c> before deletion).
/// </summary>
internal sealed class EconomyRetentionPlan(long ledgerCutoffUtcMs, long mailCutoffUtcMs, long futureUtcMs, int batch) : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "economy/retention";

    internal readonly record struct Batch(int MailCompacted, int LedgerEntriesPruned, bool More);

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long mailWatermark, prunedWatermark;
        string realm;
        using (var meta = DbSql.Command(db, tx,
            "SELECT realm_id, compacted_mail_seq, pruned_entry_seq FROM economy_meta WHERE singleton=1"))
        using (var row = meta.ExecuteReader())
        {
            if (!row.Read()) return new Batch(0, 0, false);
            realm = row.GetString(0);
            mailWatermark = row.GetInt64(1);
            prunedWatermark = row.GetInt64(2);
        }

        int mailCompacted = 0;
        bool moreMail = false;
        if (mailCutoffUtcMs > 0)
            (mailCompacted, moreMail) = CompactMailBatch(db, tx, realm, mailWatermark);

        int ledgerPruned = 0;
        bool moreLedger = false;
        if (ledgerCutoffUtcMs > 0)
            (ledgerPruned, moreLedger) = PruneLedgerBatch(db, tx, prunedWatermark);

        return new Batch(mailCompacted, ledgerPruned, moreMail || moreLedger);
    }

    private (int Compacted, bool More) CompactMailBatch(SqliteConnection db, SqliteTransaction tx, string realm, long after)
    {
        var due = new List<(string Operation, string SourceKey, byte[] Payload)>(batch);
        long watermark = after;
        int read = 0;
        bool reachedRecent = false;
        using (var scan = DbSql.Command(db, tx, """
            SELECT operation_id, commit_seq, recorded_utc_ms, source_key, intent_payload
            FROM operation_receipt
            WHERE commit_seq > $after AND command_kind >= 'mail/' AND command_kind < 'mail0'
            ORDER BY commit_seq LIMIT $batch
            """))
        {
            scan.Parameters.AddWithValue("$after", after);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                read++;
                long recorded = row.GetInt64(2);
                if (recorded >= futureUtcMs) { watermark = row.GetInt64(1); continue; }
                if (recorded >= mailCutoffUtcMs) { reachedRecent = true; break; }
                byte[] payload = (byte[])row.GetValue(4);
                if (!ReceiptBlobs.IsCompacted(payload))
                    due.Add((row.GetString(0), row.GetString(3), payload));
                watermark = row.GetInt64(1);
            }
        }

        int compacted = 0;
        foreach (var (operation, sourceKey, payload) in due)
        {
            if (TryExtractMailReplay(payload, out long role, out string deliveryKey, out string semanticHash, out string letterId))
            {
                using var save = DbSql.Command(db, tx, """
                    INSERT INTO mail_delivery_replay(realm_id, source_key, operation_id, role_id, delivery_key, semantic_hash, letter_id)
                    VALUES($realm, $source, $op, $role, $delivery, $hash, $letter)
                    ON CONFLICT(realm_id, source_key) DO NOTHING
                    """);
                save.Parameters.AddWithValue("$realm", realm);
                save.Parameters.AddWithValue("$source", sourceKey);
                save.Parameters.AddWithValue("$op", operation);
                save.Parameters.AddWithValue("$role", role);
                save.Parameters.AddWithValue("$delivery", deliveryKey);
                save.Parameters.AddWithValue("$hash", semanticHash);
                save.Parameters.AddWithValue("$letter", letterId);
                save.ExecuteNonQuery();
            }

            using (var update = DbSql.Command(db, tx, """
                UPDATE operation_receipt SET intent_payload=$marker, outcome=X''
                WHERE operation_id=$op AND intent_payload<>$marker
                """))
            {
                update.Parameters.AddWithValue("$marker", ReceiptBlobs.CompactedPayload);
                update.Parameters.AddWithValue("$op", operation);
                compacted += update.ExecuteNonQuery();
            }
            using var dropOwner = DbSql.Command(db, tx, "DELETE FROM owner_operation_receipt WHERE operation_id=$op");
            dropOwner.Parameters.AddWithValue("$op", operation);
            dropOwner.ExecuteNonQuery();
        }

        if (watermark > after)
        {
            using var advance = DbSql.Command(db, tx,
                "UPDATE economy_meta SET compacted_mail_seq=$seq WHERE singleton=1 AND compacted_mail_seq<$seq");
            advance.Parameters.AddWithValue("$seq", watermark);
            advance.ExecuteNonQuery();
        }
        return (compacted, !reachedRecent && read == batch);
    }

    private (int Pruned, bool More) PruneLedgerBatch(SqliteConnection db, SqliteTransaction tx, long after)
    {
        long watermark = after;
        int read = 0;
        bool reachedRecent = false;
        using (var scan = DbSql.Command(db, tx, """
            SELECT commit_seq, recorded_utc_ms
            FROM operation_receipt
            WHERE commit_seq > $after
            ORDER BY commit_seq LIMIT $batch
            """))
        {
            scan.Parameters.AddWithValue("$after", after);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                read++;
                long recorded = row.GetInt64(1);
                if (recorded >= futureUtcMs) { watermark = row.GetInt64(0); continue; }
                if (recorded >= ledgerCutoffUtcMs) { reachedRecent = true; break; }
                watermark = row.GetInt64(0);
            }
        }

        if (watermark <= after) return (0, false);

        using (var agg = DbSql.Command(db, tx, """
            INSERT INTO economy_daily_aggregate(day_utc, asset_kind, asset_id, reason_code, leg_kind, entry_count, net_delta, abs_delta)
            SELECT strftime('%Y-%m-%d', recorded_utc_ms / 1000, 'unixepoch'),
                   asset_kind, asset_id, reason_code, leg_kind,
                   COUNT(*), SUM(delta), SUM(ABS(delta))
            FROM economy_entry
            WHERE commit_seq > $after AND commit_seq <= $upto AND recorded_utc_ms < $cutoff
            GROUP BY 1, asset_kind, asset_id, reason_code, leg_kind
            ON CONFLICT(day_utc, asset_kind, asset_id, reason_code, leg_kind) DO UPDATE SET
                entry_count = entry_count + excluded.entry_count,
                net_delta = net_delta + excluded.net_delta,
                abs_delta = abs_delta + excluded.abs_delta
            """))
        {
            agg.Parameters.AddWithValue("$after", after);
            agg.Parameters.AddWithValue("$upto", watermark);
            agg.Parameters.AddWithValue("$cutoff", ledgerCutoffUtcMs);
            agg.ExecuteNonQuery();
        }

        int pruned;
        using (var del = DbSql.Command(db, tx, """
            DELETE FROM economy_entry
            WHERE commit_seq > $after AND commit_seq <= $upto AND recorded_utc_ms < $cutoff
            """))
        {
            del.Parameters.AddWithValue("$after", after);
            del.Parameters.AddWithValue("$upto", watermark);
            del.Parameters.AddWithValue("$cutoff", ledgerCutoffUtcMs);
            pruned = del.ExecuteNonQuery();
        }

        using (var advance = DbSql.Command(db, tx,
            "UPDATE economy_meta SET pruned_entry_seq=$seq WHERE singleton=1 AND pruned_entry_seq<$seq"))
        {
            advance.Parameters.AddWithValue("$seq", watermark);
            advance.ExecuteNonQuery();
        }
        return (pruned, !reachedRecent && read == batch);
    }

    private static bool TryExtractMailReplay(byte[] payload, out long role, out string deliveryKey,
        out string semanticHash, out string letterId)
    {
        role = 0; deliveryKey = ""; semanticHash = ""; letterId = "";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("Evidence", out var evidences) || evidences.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var evidence in evidences.EnumerateArray())
            {
                if (!evidence.TryGetProperty("Json", out var stored) || stored.ValueKind != JsonValueKind.String)
                    continue;
                using var detail = JsonDocument.Parse(stored.GetString()!);
                var root = detail.RootElement;
                if (!root.TryGetProperty("DeliverySemanticHash", out var hashProp)) continue;
                role = root.GetProperty("Role").GetInt64();
                deliveryKey = root.GetProperty("DeliveryKey").GetString() ?? "";
                semanticHash = hashProp.GetString() ?? "";
                letterId = root.GetProperty("LetterId").GetString() ?? "";
                return letterId.Length is >= 1 and <= 32 && letterId.All(char.IsAsciiDigit);
            }
            return false;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
