using Microsoft.AspNetCore.Builder;
using Ro3Emu.Bot;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

/// <summary>
/// A small hand-written game data set (Fixtures/GameData, copied next to the test binaries) that stands in for the
/// tables exported from the client. Every value in it is invented; it holds only what the server needs to boot, let a
/// bot log in, create a role and enter the world, plus the rows the fixture-based tests read. A test that checks a rule
/// rather than real content (a failed save, a retry after a restart, who owns what) points GameDataDir here and runs in
/// a public checkout and in CI; a test about the real content keeps <see cref="ClientDataFactAttribute"/>.
/// See docs/guide/testing.md.
/// </summary>
public static class SyntheticData
{
    /// <summary>The fixture root, used as <see cref="ServerOptions.GameDataDir"/> (config/, sceneeditor/, msgtable.tsv).</summary>
    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameData");

    /// <summary>The fixture's only open-world map; new roles are born there (RoleStore.SpawnSceneEntry).</summary>
    public const long Town = 10111;
}

/// <summary>
/// Compatibility wrapper over <see cref="TestServer"/> on the synthetic fixture.
/// </summary>
public sealed class SyntheticHost : IAsyncDisposable
{
    private readonly TestServer _server;

    private SyntheticHost(TestServer server) => _server = server;

    public string DataDir => _server.DataDir;
    public WebApplication App => _server.App;
    public string Http => _server.Http;

    public static async Task<SyntheticHost> StartAsync(Action<ServerOptions>? configure = null, TimeProvider? clock = null) =>
        new(await TestServer.StartSyntheticAsync(configure, clock));

    public T Service<T>() where T : notnull => _server.Service<T>();
    public BotClient NewBot() => _server.NewBot();
    public Task<BotClient> InWorldAsync(string name, bool create = true) => _server.InWorldAsync(name, create);
    public Task RestartAsync() => _server.RestartAsync();
    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
