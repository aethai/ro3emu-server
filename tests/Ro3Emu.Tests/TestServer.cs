using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Shared in-process server fixture for integration tests: isolated temp data dir, free loopback ports,
/// injected <see cref="TimeProvider"/>, synthetic or exported client data, bot helpers, and clean restart/disposal
/// without needing <c>SqliteConnection.ClearAllPools()</c>.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private readonly string[] _args;
    private readonly string _gameDataDir;
    private readonly Action<ServerOptions>? _configure;
    private readonly TimeProvider? _clock;
    private readonly bool _ownsDataDir;

    private TestServer(string dataDir, string gameDataDir, string[] args,
        Action<ServerOptions>? configure, TimeProvider? clock, bool ownsDataDir)
    {
        DataDir = dataDir;
        _gameDataDir = gameDataDir;
        _args = args;
        _configure = configure;
        _clock = clock;
        _ownsDataDir = ownsDataDir;
    }

    public string DataDir { get; }
    public WebApplication App { get; private set; } = null!;
    public string Http { get; private set; } = "";
    public int HttpPort { get; private set; }
    public int MicroGatePort { get; private set; }
    public int GameGatePort { get; private set; }
    public TimeProvider Clock => Service<TimeProvider>();
    public FakeTimeProvider? FakeClock => Clock as FakeTimeProvider;
    public GameLoop Loop => Service<GameLoop>();

    public static FakeTimeProvider CreateFakeClock(DateTimeOffset? start = null) =>
        new(start ?? new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Advances the server's <see cref="FakeTimeProvider"/> and executes due game-loop timers and tickers.</summary>
    public Task AdvanceAsync(TimeSpan duration)
    {
        var fake = FakeClock ?? throw new InvalidOperationException("TestServer was not started with a FakeTimeProvider.");
        return Loop.AdvanceAsync(duration, fake.Advance);
    }

    /// <summary>Starts a server on the synthetic fixture (<see cref="SyntheticData.Dir"/>) so it runs in CI without client data.</summary>
    public static Task<TestServer> StartSyntheticAsync(Action<ServerOptions>? configure = null, TimeProvider? clock = null,
        string? dataDir = null, string[]? args = null) =>
        StartAsync(SyntheticData.Dir, configure, clock, dataDir, args, "synthetic");

    /// <summary>Builds a synthetic server without starting hosted services yet; call <see cref="StartAsync()"/> to start it.</summary>
    public static TestServer CreateSyntheticUnstarted(Action<ServerOptions>? configure = null, TimeProvider? clock = null,
        string? dataDir = null, string[]? args = null)
    {
        bool owns = dataDir == null;
        string dir = dataDir ?? Path.Combine(Path.GetTempPath(), $"ro3emu-synthetic-{Guid.NewGuid():N}");
        var server = new TestServer(dir, SyntheticData.Dir, args ?? [], configure, clock, owns);
        server.BuildHost();
        return server;
    }

    public async Task StartAsync()
    {
        await App.StartAsync();
        Http = $"http://127.0.0.1:{HttpPort}";
    }

    /// <summary>Starts a server on the exported client game data (<see cref="ClientData.RepoRoot"/>/data).</summary>
    public static Task<TestServer> StartClientAsync(Action<ServerOptions>? configure = null, TimeProvider? clock = null,
        string? dataDir = null, string[]? args = null) =>
        StartAsync(Path.Combine(ClientData.RepoRoot, "data"), configure, clock, dataDir, args, "client");

    private static async Task<TestServer> StartAsync(string gameDataDir, Action<ServerOptions>? configure, TimeProvider? clock,
        string? dataDir, string[]? args, string prefix)
    {
        bool owns = dataDir == null;
        string dir = dataDir ?? Path.Combine(Path.GetTempPath(), $"ro3emu-{prefix}-{Guid.NewGuid():N}");
        var server = new TestServer(dir, gameDataDir, args ?? [], configure, clock, owns);
        await server.StartCoreAsync();
        return server;
    }

    private void BuildHost()
    {
        (HttpPort, MicroGatePort, GameGatePort) = TestPorts.FreePorts();
        App = ServerHost.Create(_args, o =>
        {
            o.DataDir = DataDir;
            o.GameDataDir = _gameDataDir;
            o.HttpPort = HttpPort;
            o.MicroGatePort = MicroGatePort;
            o.GameGatePort = GameGatePort;
            _configure?.Invoke(o);
        }, _clock);
    }

    private async Task StartCoreAsync()
    {
        BuildHost();
        await StartAsync();
    }

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public Task OnLoopAsync(Func<Task> work) => Loop.InvokeAsync(work);
    public Task<T> OnLoopAsync<T>(Func<Task<T>> work) => Loop.InvokeAsync(work);
    public Task OnLoopAsync(Action work) => Loop.InvokeAsync(() => { work(); return Task.CompletedTask; });

    /// <summary>A bot that has not logged in yet.</summary>
    public BotClient NewBot() =>
        new(Http, File.ReadAllText(Path.Combine(DataDir, "gate_rsa_public.pem"))) { OperatorToken = Operator.Token };

    /// <summary>Logs in, creates (or selects) a role, and enters the world.</summary>
    public async Task<BotClient> InWorldAsync(string name, bool create = true, string password = "pw", string? account = null)
    {
        var bot = NewBot();
        await bot.LoginAsync(account ?? name.ToLowerInvariant(), password);
        await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    public GameSession Session(string account) =>
        Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    public GameSession Session(BotClient bot) => Session(bot.Account!);

    public GameSession SessionByRole(long roleId) =>
        Service<SessionRegistry>().All().Single(s => s.Role?.Id == roleId);

    public async Task GmAsync(string account, string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{Http}/ro3emu/gm?account={Uri.EscapeDataString(account)}",
            Operator.Command(command))).EnsureSuccessStatusCode();
    }

    public Task GmAsync(BotClient bot, string command) => GmAsync(Session(bot).Account!, command);

    public static async Task<T> NextAsync<T>(BotClient bot, uint msgId, MessageParser<T> parser,
        Func<T, bool>? match = null, TimeSpan? timeout = null) where T : IMessage<T>
    {
        var envelope = await bot.Inbox.WaitAsync(
            e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))),
            timeout ?? DefaultWait);
        return parser.ParseFrom(envelope.Body);
    }

    public async Task UntilAsync(Func<bool> done, string what, TimeSpan? timeout = null)
    {
        var loop = Loop;
        for (var deadline = DateTime.UtcNow + (timeout ?? DefaultWait); ; await Task.Delay(20))
        {
            bool ok = false;
            await loop.InvokeAsync(() => { ok = done(); return Task.CompletedTask; });
            if (ok) return;
            Assert.True(DateTime.UtcNow < deadline, what);
        }
    }

    /// <summary>Stops the server and starts a new one on the same data directory (new ports).</summary>
    public async Task RestartAsync()
    {
        await StopCoreAsync();
        await StartCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync();
        if (_ownsDataDir && Directory.Exists(DataDir))
        {
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
        }
    }
}
