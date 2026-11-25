using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

public sealed class DatabaseMigrationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-migrations-" + Guid.NewGuid().ToString("N"));

    public DatabaseMigrationsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AccountStore OpenStore() =>
        new(Options.Create(new ServerOptions { DataDir = _dir, GameDataDir = SyntheticData.Dir }));

    private string DbPath => Path.Combine(_dir, "ro3emu.db");

    private int ReadUserVersion()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        db.Open();
        return DatabaseMigrations.ReadVersion(db);
    }

    private void ExecRaw(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long QueryLong(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void FreshDatabaseAppliesMigrationsOnceAndSecondOpenRunsZero()
    {
        using (var first = OpenStore())
        {
            Assert.Equal(DatabaseMigrations.All.Count, first.AppliedMigrations);
            Assert.Equal(DatabaseMigrations.CurrentVersion, ReadUserVersion());
        }

        using (var second = OpenStore())
        {
            Assert.Equal(0, second.AppliedMigrations);
            Assert.Equal(DatabaseMigrations.CurrentVersion, ReadUserVersion());
        }
    }

    [Theory]
    [InlineData("early_release")]
    [InlineData("mid_release")]
    [InlineData("pre_versioning_release")]
    public void HistoricalReleaseFixturesMigrateCleanlyAndSecondOpenRunsZero(string fixture)
    {
        SeedReleaseFixture(fixture);
        Assert.Equal(0, ReadUserVersion());

        using (var upgraded = OpenStore())
        {
            Assert.Equal(DatabaseMigrations.All.Count, upgraded.AppliedMigrations);
            Assert.Equal(DatabaseMigrations.CurrentVersion, ReadUserVersion());

            // Preserved role, account, lifecycle, and revisions.
            Assert.Equal(1, QueryLong("SELECT COUNT(*) FROM account WHERE name = 'alice'"));
            Assert.Equal(1, QueryLong("SELECT COUNT(*) FROM role WHERE id = 100001 AND name = 'Hero'"));
            Assert.Equal(1, QueryLong("SELECT COUNT(*) FROM role_lifecycle WHERE role_id = 100001 AND deleted = 0"));
            Assert.True(QueryLong("SELECT COUNT(*) FROM owner_revision WHERE kind = 'role' AND id = '100001'") > 0);

            if (fixture == "early_release")
            {
                // Legacy item.bind -> bind_type and item.expire_time -> bind_expire migrated cleanly.
                Assert.Equal(2, QueryLong("SELECT bind_type FROM item WHERE guid = 1000001"));
                Assert.Equal(1800000000, QueryLong("SELECT bind_expire FROM item WHERE guid = 1000001"));
            }
            if (fixture == "pre_versioning_release")
            {
                // Existing non-zero owner_revision must not be overwritten by baseline backfill.
                Assert.Equal(7, QueryLong("SELECT revision FROM owner_revision WHERE kind = 'role' AND id = '100001' AND part = 'progress'"));
            }
        }

        using (var reopened = OpenStore())
        {
            Assert.Equal(0, reopened.AppliedMigrations);
            Assert.Equal(DatabaseMigrations.CurrentVersion, ReadUserVersion());
        }
    }

    [Fact]
    public void NewerSchemaVersionRefusesToOpen()
    {
        using (var store = OpenStore())
        {
            Assert.Equal(DatabaseMigrations.CurrentVersion, ReadUserVersion());
        }
        ExecRaw("PRAGMA user_version = 999;");

        var error = Assert.Throws<InvalidOperationException>(() => _ = OpenStore());
        Assert.Contains("999", error.Message, StringComparison.Ordinal);
    }

    private void SeedReleaseFixture(string fixture)
    {
        if (fixture == "early_release")
        {
            ExecRaw("""
                CREATE TABLE account (name TEXT PRIMARY KEY, password_hash BLOB NOT NULL, salt BLOB NOT NULL, created_utc TEXT NOT NULL);
                INSERT INTO account VALUES ('alice', X'01', X'02', '2026-01-01T00:00:00Z');
                CREATE TABLE role (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    account TEXT NOT NULL,
                    area INTEGER NOT NULL,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    sex INTEGER NOT NULL,
                    career INTEGER NOT NULL,
                    level INTEGER NOT NULL,
                    hair INTEGER NOT NULL,
                    create_time INTEGER NOT NULL,
                    last_login_time INTEGER NOT NULL
                );
                INSERT INTO role VALUES (100001, 'alice', 1, 'Hero', 1, 1000, 25, 1, 1700000000, 1700000100);
                CREATE TABLE item (
                    guid INTEGER PRIMARY KEY AUTOINCREMENT,
                    role_id INTEGER NOT NULL,
                    entry INTEGER NOT NULL,
                    stack INTEGER NOT NULL,
                    pack INTEGER NOT NULL,
                    sort INTEGER NOT NULL,
                    grid INTEGER NOT NULL DEFAULT 0,
                    bind INTEGER NOT NULL DEFAULT 0,
                    expire_time INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO item VALUES (1000001, 100001, 2001001, 1, 2, 1, 0, 2, 1800000000);
                """);
            return;
        }

        if (fixture == "mid_release")
        {
            ExecRaw("""
                CREATE TABLE account (name TEXT PRIMARY KEY, password_hash BLOB NOT NULL, salt BLOB NOT NULL, created_utc TEXT NOT NULL);
                INSERT INTO account VALUES ('alice', X'01', X'02', '2026-06-01T00:00:00Z');
                CREATE TABLE role (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    account TEXT NOT NULL,
                    area INTEGER NOT NULL,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    sex INTEGER NOT NULL,
                    career INTEGER NOT NULL,
                    level INTEGER NOT NULL,
                    hair INTEGER NOT NULL,
                    create_time INTEGER NOT NULL,
                    last_login_time INTEGER NOT NULL,
                    job_level INTEGER NOT NULL DEFAULT 1,
                    exp INTEGER NOT NULL DEFAULT 0,
                    job_exp INTEGER NOT NULL DEFAULT 0,
                    deleted_time INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO role (id, account, area, name, sex, career, level, hair, create_time, last_login_time, job_level)
                VALUES (100001, 'alice', 1, 'Hero', 1, 1000, 40, 1, 1700000000, 1700000200, 20);
                CREATE TABLE auction_item (id INTEGER PRIMARY KEY AUTOINCREMENT, source_key TEXT NOT NULL UNIQUE, state BLOB NOT NULL);
                CREATE TABLE role_lifecycle (role_id INTEGER PRIMARY KEY, epoch INTEGER NOT NULL CHECK(epoch > 0), deleted INTEGER NOT NULL CHECK(deleted IN (0,1)));
                INSERT INTO role_lifecycle VALUES (100001, 3, 0);
                """);
            return;
        }

        // pre_versioning_release: full current schema created by previous unversioned release, user_version still 0.
        using (var store = OpenStore())
        {
            Assert.Equal(DatabaseMigrations.CurrentVersion, ReadUserVersion());
        }
        ExecRaw("""
            INSERT INTO account VALUES ('alice', X'01', X'02', '2026-11-01T00:00:00Z');
            INSERT INTO role (id, account, area, name, sex, career, level, hair, create_time, last_login_time)
            VALUES (100001, 'alice', 1, 'Hero', 1, 1000, 60, 1, 1700000000, 1700000300);
            INSERT INTO role_lifecycle VALUES (100001, 1, 0);
            INSERT INTO owner_revision VALUES ('role', '100001', 'progress', 7);
            PRAGMA user_version = 0;
            """);
    }
}
