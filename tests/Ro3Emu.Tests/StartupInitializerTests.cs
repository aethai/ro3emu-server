using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class StartupInitializerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-startup-init-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task StartupDbIoRunsInStartupPhaseNotConstructors()
    {
        Directory.CreateDirectory(_dir);
        string dbPath;
        long roleId;
        using (var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir })))
        {
            dbPath = accounts.DatabasePath;
            var roles = new RoleStore(accounts);
            var role = roles.Create("startup-init-acc", 1, "InitRole", 1, 1000, 1)
                ?? throw new InvalidOperationException("Failed to create test role.");
            roleId = role.Id;
        }

        // Seed one legacy market_record mail row for the real role so MarketStore's migration is observable.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            db.Open();
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO market_record(kind, id, data) VALUES('mail', 'legacy-1', $d)";
            insert.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new
            {
                Id = "legacy-1",
                RoleId = roleId,
                TemplateId = 1063,
                CreatedTime = 1700000000L,
                Claimed = true,
                Items = Array.Empty<object>(),
                Currency = new Dictionary<int, long>(),
                ContentParams = Array.Empty<string>(),
            }));
            insert.ExecuteNonQuery();
        }

        await using var server = TestServer.CreateSyntheticUnstarted(options =>
        {
            options.PreloadGameData = false;
            options.Panel.Enabled = false;
        }, dataDir: _dir);

        var initializers = server.App.Services.GetServices<IStartupInitializer>().Select(x => x.GetType()).ToHashSet();
        Assert.Contains(typeof(MarketStore), initializers);
        Assert.Contains(typeof(FestivalService), initializers);
        Assert.Contains(typeof(GuildLeagueService), initializers);
        Assert.Contains(typeof(TerritoryEscortService), initializers);
        Assert.Contains(typeof(TerritoryWarsService), initializers);
        Assert.Contains(typeof(FlyingDragonService), initializers);
        Assert.Contains(typeof(RankRewardService), initializers);
        Assert.Contains(typeof(GuildHuntingService), initializers);

        // Constructing DI services (including MarketStore) must not have migrated legacy mail yet.
        Assert.Equal(1, CountLegacyMailRows(dbPath));

        await server.StartAsync();

        // RealmStartupService ran all IStartupInitializer instances before loop and gates opened.
        Assert.Equal(0, CountLegacyMailRows(dbPath));
    }

    [Fact]
    public void NoGameServiceConstructorPerformsSyncDatabaseIo()
    {
        string gameDir = Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.Server", "Game");
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(gameDir, "*.cs", SearchOption.AllDirectories))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var root = tree.GetRoot();
            foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            {
                foreach (var call in ctor.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string name = call.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        IdentifierNameSyntax id => id.Identifier.ValueText,
                        GenericNameSyntax g => g.Identifier.ValueText,
                        _ => "",
                    };
                    if (name is "ReadSync" or "WriteSync" or "MigrateLegacyMail")
                    {
                        int line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        violations.Add($"{Path.GetRelativePath(gameDir, file)}:{line} calls {name} inside constructor");
                    }
                }
            }
        }
        Assert.True(violations.Count == 0,
            "Game service constructors must not execute synchronous database I/O:\n" + string.Join('\n', violations));
    }

    private static int CountLegacyMailRows(string dbPath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM market_record WHERE kind='mail'";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
