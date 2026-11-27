using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed record SchemaMigration(int Version, string Name, Action<SqliteConnection, SqliteTransaction> Apply);

/// <summary>
/// Versioned SQLite schema migrations keyed by <c>PRAGMA user_version</c>.
/// Each migration runs inside a single transaction that stamps <c>user_version</c> atomically; subsequent startups
/// read <c>user_version == CurrentVersion</c> and perform zero DDL or backfill work.
/// </summary>
internal static partial class DatabaseMigrations
{
    internal static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new(1, "baseline_schema", ApplyBaselineSchema),
        new(2, "anticheat_gains_index", ApplyAntiCheatGainsIndex),
        new(3, "retention_tables", ApplyRetentionTables),
        new(4, "trade_listing_table", ApplyTradeListingTable),
        new(5, "operation_receipt_mail_index", ApplyOperationReceiptMailIndex),
    ];

    internal static int CurrentVersion => All[^1].Version;

    internal static int ReadVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static int Migrate(SqliteConnection db, ILogger? log = null)
    {
        int current = ReadVersion(db);
        if (current > CurrentVersion)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Database schema version {current} is newer than supported version {CurrentVersion}."));
        if (current == CurrentVersion) return 0;

        int applied = 0;
        foreach (var migration in All)
        {
            if (migration.Version <= current) continue;
            // A migration that builds an index or copies a table reads the whole table: on a large database (a 47 GB one
            // took 22 minutes for v5) the server opens no port meanwhile, so say what it is doing before it starts.
            if (log != null) LogMigrationStarting(log, migration.Version, migration.Name);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var tx = db.BeginTransaction();
            migration.Apply(db, tx);
            SchemaSql.Execute(db, tx, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};"));
            tx.Commit();
            applied++;
            if (log != null) LogMigrationApplied(log, migration.Version, migration.Name, clock.Elapsed.TotalSeconds);
        }
        return applied;
    }

    private static void ApplyBaselineSchema(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS account (
                name          TEXT PRIMARY KEY,
                password_hash BLOB NOT NULL,
                salt          BLOB NOT NULL,
                created_utc   TEXT NOT NULL
            );
            -- Login finds an account in any letter case (AccountCredentialsRead); not unique, legacy spellings may coexist.
            CREATE INDEX IF NOT EXISTS account_name_nocase ON account(name COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS auth_token (
                token       TEXT PRIMARY KEY,
                kind        TEXT NOT NULL,
                account     TEXT NOT NULL,
                expires_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS owner_revision (
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                part TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 0),
                PRIMARY KEY(kind,id,part)
            );
            CREATE TABLE IF NOT EXISTS owner_operation_receipt (
                operation_id TEXT PRIMARY KEY,
                family TEXT NOT NULL,
                result BLOB NOT NULL
            );
            """);
        foreach (var plan in DatabaseSchema.Plans())
            plan.Execute(db, tx);
    }

    private static void ApplyAntiCheatGainsIndex(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS economy_entry_role_sources
                ON economy_entry(recorded_utc_ms, owner_id, asset_kind, asset_id, delta)
                WHERE owner_kind=1 AND leg_kind=1 AND delta>0;
            """);
    }

    private static void ApplyRetentionTables(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS economy_daily_aggregate (
                day_utc TEXT NOT NULL,
                asset_kind INTEGER NOT NULL,
                asset_id INTEGER NOT NULL,
                reason_code INTEGER NOT NULL,
                leg_kind INTEGER NOT NULL,
                entry_count INTEGER NOT NULL,
                net_delta INTEGER NOT NULL,
                abs_delta INTEGER NOT NULL,
                PRIMARY KEY(day_utc, asset_kind, asset_id, reason_code, leg_kind)
            );
            CREATE TABLE IF NOT EXISTS mail_delivery_replay (
                realm_id TEXT NOT NULL,
                source_key TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                role_id INTEGER NOT NULL,
                delivery_key TEXT NOT NULL,
                semantic_hash TEXT NOT NULL,
                letter_id TEXT NOT NULL,
                PRIMARY KEY(realm_id, source_key)
            );
            """);
        SchemaSql.AddColumn(db, tx, "economy_meta", "pruned_entry_seq", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "economy_meta", "compacted_mail_seq", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void ApplyTradeListingTable(SqliteConnection db, SqliteTransaction tx)
    {
        TradeListingSql.MigrateFromMarketRecord(db, tx);
    }

    private static void ApplyOperationReceiptMailIndex(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS operation_receipt_mail_seq
                ON operation_receipt(commit_seq)
                WHERE command_kind >= 'mail/' AND command_kind < 'mail0';
            """);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Applying SQLite schema migration v{Version} ({Name}); on a large database this can take minutes, and the server opens its ports when it is done")]
    private static partial void LogMigrationStarting(ILogger logger, int version, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied SQLite schema migration v{Version} ({Name}) in {Seconds:0.0} s")]
    private static partial void LogMigrationApplied(ILogger logger, int version, string name, double seconds);
}
