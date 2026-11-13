using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>A role id is a unit uuid the client also looks up in NPCConfig (DPS meter names), so it must never be one.</summary>
public sealed class RoleIdFloorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-role-id-floor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public void NewRealmHandsOutRoleIdsAboveTheFloor()
    {
        using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        long id = new RoleStore(accounts).Create("floor", 1, "Floored", 1, 1000, 1)!.Id;
        Assert.True(id > RoleStore.RoleIdFloor, $"role id {id}");
    }

    [Fact]
    public void OlderRealmKeepsItsRolesAndNextRoleStartsAboveTheFloor()
    {
        var options = Options.Create(new ServerOptions { DataDir = _directory });
        long old;
        using (var legacy = new AccountStore(options))
        {
            // An older realm counted from 100000, so its first role took id 100001 (NPCConfig "Kafra Staff").
            FixtureSql.Execute(legacy, "UPDATE sqlite_sequence SET seq = 100000 WHERE name = 'role'");
            old = new RoleStore(legacy).Create("legacy", 1, "Legacy", 1, 1000, 1)!.Id;
            Assert.Equal(100001, old);
        }
        using var accounts = new AccountStore(options);
        var roles = new RoleStore(accounts);
        Assert.Equal("Legacy", roles.Get(old)?.Name);
        Assert.True(roles.Create("legacy", 1, "Fresh", 1, 1000, 1)!.Id > RoleStore.RoleIdFloor);
    }

    [ClientDataFact]
    public void FloorIsAboveEveryNpcConfigIdAndFitsInt32()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Ro3Emu.slnx"))) root = root.Parent;
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = Path.Combine(root!.FullName, "data") }),
            NullLogger<GameData>.Instance);
        Assert.NotEmpty(data.Table("NPCConfig"));
        Assert.All(data.Table("NPCConfig").Keys, key => Assert.True(long.Parse(key, System.Globalization.CultureInfo.InvariantCulture) < RoleStore.RoleIdFloor, key));
        Assert.True(RoleStore.RoleIdFloor < int.MaxValue);
    }
}
