using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// One batch of <see cref="ReceiptRetention"/>: walks the receipts after the stored watermark
/// (<c>economy_meta.compacted_seq</c>) in commit order, at most <paramref name="batch"/> of them, and stops at the first
/// one recorded at or after <paramref name="cutoffUtcMs"/>. The due ones lose their intent payload (replaced by
/// <see cref="ReceiptBlobs.CompactedPayload"/>), their outcome bytes, their child results and revision vectors, and their
/// owner acknowledgement (<c>owner_operation_receipt</c>), and the watermark moves past every receipt the batch walked,
/// so the next run starts where this one stopped instead of rescanning the realm's whole history.
/// - **Kept:** the row, its sequence, hashes (intent_hash covers the outcome), leg count, ledger legs and source claim,
///   so idempotency and the ledger's continuity are unchanged. A replay of a compacted operation is refused as a
///   conflict instead of answered (<see cref="EconomyTransactionPlan.DecodeChildReceipts"/>): replays follow a lost
///   acknowledgement or a crash within seconds, and kept forever the child results and owner acknowledgements of every
///   monster kill grew a database by about 3 GB a week (issue #473).
/// - **Not compacted:** <c>mail/</c> deliveries. <see cref="MailDeliveryReceiptReadPlan"/> re-reads a delivery's payload
///   whenever the same delivery key comes back, however late. Escrow receipts (auction and market) keep their child
///   results, which their own codec reads. The watermark still moves past them.
/// - **Older databases:** receipts compacted before child results were dropped sit behind the watermark. A second
///   watermark (<c>economy_meta.compacted_full_seq</c>) walks them once, in the same small batches, before new ones.
/// Receipts carry the wall clock of their commit, so commit order is time order only while that clock runs forward. A
/// receipt that is merely not due yet ends the walk, since what follows it is about as young. One dated at or after
/// <paramref name="futureUtcMs"/> was written under a wrong clock: the watermark moves past it without compacting it,
/// as its real age is unknown and one receipt must not stop compaction until its date arrives. Later due receipts
/// behind a date that is not that far off wait for it, at most a day past their own due time.
/// </summary>
internal sealed class ReceiptCompactionPlan(long cutoffUtcMs, long futureUtcMs, int batch) : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "economy/receipt-compaction";

    /// <summary>A batch's result: receipts compacted, receipts the watermark moved past, how many of those carried an
    /// impossible future date, whether more may be due, and older compacted receipts whose child results it dropped.</summary>
    internal readonly record struct Batch(int Compacted, int Passed, int Future, bool More, int Backfilled = 0);

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long after, full;
        using (var meta = DbSql.Command(db, tx, "SELECT compacted_seq,compacted_full_seq FROM economy_meta WHERE singleton=1"))
        using (var row = meta.ExecuteReader())
            (after, full) = row.Read() ? (row.GetInt64(0), row.GetInt64(1)) : (0, 0);
        if (full < after) return Backfill(db, tx, full, after);
        var due = new List<(string Operation, string Kind)>(batch);
        long watermark = after;
        int read = 0, passed = 0, future = 0;
        bool reachedRecent = false;
        using (var scan = DbSql.Command(db, tx, """
            SELECT operation_id,commit_seq,recorded_utc_ms,command_kind FROM operation_receipt
            WHERE commit_seq>$after ORDER BY commit_seq LIMIT $batch
            """))
        {
            scan.Parameters.AddWithValue("$after", after);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                read++;
                long recorded = row.GetInt64(2);
                if (recorded >= futureUtcMs) future++;
                else if (recorded >= cutoffUtcMs) { reachedRecent = true; break; }
                else if (!row.GetString(3).StartsWith("mail/", StringComparison.Ordinal)) due.Add((row.GetString(0), row.GetString(3)));
                watermark = row.GetInt64(1);
                passed++;
            }
        }
        int compacted = 0;
        using (var update = DbSql.Command(db, tx, """
            UPDATE operation_receipt SET intent_payload=$marker,outcome=X''
            WHERE operation_id=$op AND intent_payload<>$marker
            """))
        {
            update.Parameters.AddWithValue("$marker", ReceiptBlobs.CompactedPayload);
            var op = update.Parameters.Add("$op", SqliteType.Text);
            foreach (var (id, _) in due)
            {
                op.Value = id;
                compacted += update.ExecuteNonQuery();
            }
        }
        DropReplayData(db, tx, due);
        if (watermark > after)
        {
            using var advance = DbSql.Command(db, tx, """
                UPDATE economy_meta SET compacted_seq=$seq,compacted_full_seq=$seq WHERE singleton=1 AND compacted_seq<$seq
                """);
            advance.Parameters.AddWithValue("$seq", watermark);
            advance.ExecuteNonQuery();
        }
        return new Batch(compacted, passed, future, !reachedRecent && read == batch);
    }

    /// <summary>Receipts compacted before child results were dropped: the walk of <c>compacted_full_seq</c> up to
    /// <c>compacted_seq</c>. Only those whose payload was compacted are touched; mail and misdated ones kept theirs.</summary>
    private Batch Backfill(SqliteConnection db, SqliteTransaction tx, long full, long after)
    {
        var compacted = new List<(string Operation, string Kind)>(batch);
        long watermark = full;
        int read = 0;
        using (var scan = DbSql.Command(db, tx, """
            SELECT operation_id,commit_seq,command_kind,intent_payload=$marker FROM operation_receipt
            WHERE commit_seq>$full AND commit_seq<=$after ORDER BY commit_seq LIMIT $batch
            """))
        {
            scan.Parameters.AddWithValue("$marker", ReceiptBlobs.CompactedPayload);
            scan.Parameters.AddWithValue("$full", full);
            scan.Parameters.AddWithValue("$after", after);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                read++;
                watermark = row.GetInt64(1);
                if (row.GetInt64(3) == 1) compacted.Add((row.GetString(0), row.GetString(2)));
            }
        }
        int dropped = DropReplayData(db, tx, compacted);
        if (read < batch) watermark = after; // nothing left behind the compaction watermark
        using (var advance = DbSql.Command(db, tx,
            "UPDATE economy_meta SET compacted_full_seq=$seq WHERE singleton=1 AND compacted_full_seq<$seq"))
        {
            advance.Parameters.AddWithValue("$seq", watermark);
            advance.ExecuteNonQuery();
        }
        return new Batch(0, 0, 0, More: true, Backfilled: dropped);
    }

    /// <summary>The child results, revision vectors and owner acknowledgement of compacted receipts; escrow receipts keep
    /// their child results. Returns how many receipts lost their child results.</summary>
    private static int DropReplayData(SqliteConnection db, SqliteTransaction tx, List<(string Operation, string Kind)> receipts)
    {
        int dropped = 0;
        using var children = DbSql.Command(db, tx, """
            UPDATE operation_receipt SET child_receipts=$marker,before_revisions='',after_revisions=''
            WHERE operation_id=$op AND child_receipts<>$marker
            """);
        children.Parameters.AddWithValue("$marker", ReceiptBlobs.CompactedPayload);
        var child = children.Parameters.Add("$op", SqliteType.Text);
        using var owner = DbSql.Command(db, tx, "DELETE FROM owner_operation_receipt WHERE operation_id=$op");
        var acknowledgement = owner.Parameters.Add("$op", SqliteType.Text);
        foreach (var (id, kind) in receipts)
        {
            acknowledgement.Value = id;
            owner.ExecuteNonQuery();
            if (EscrowEconomyIntent.IsKind(kind)) continue;
            child.Value = id;
            dropped += children.ExecuteNonQuery();
        }
        return dropped;
    }
}
