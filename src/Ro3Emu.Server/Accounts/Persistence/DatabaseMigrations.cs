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
            using var tx = db.BeginTransaction();
            migration.Apply(db, tx);
            SchemaSql.Execute(db, tx, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Version};"));
            tx.Commit();
            applied++;
            if (log != null) LogMigrationApplied(log, migration.Version, migration.Name);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied SQLite schema migration v{Version} ({Name})")]
    private static partial void LogMigrationApplied(ILogger logger, int version, string name);
}
