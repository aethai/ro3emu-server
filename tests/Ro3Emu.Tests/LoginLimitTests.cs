using System.Net;
using Ro3Emu.Server;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>Credential checks over HTTP have a per-address budget (each one is a PBKDF2 hash and may register).</summary>
public sealed class LoginLimitTests
{
    /// <summary>
    /// The budget is a fixed window per wall-clock minute (CredentialLimiter): on the system clock the attempts of a
    /// loaded run, each a PBKDF2 hash, could straddle a minute boundary and start a fresh budget mid-test.
    /// </summary>
    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 10, 15, 12, 0, 30, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<List<HttpStatusCode>> Logins(bool limitLoopback, int attempts, string? forwardedFor = null,
        Action<ServerOptions>? configure = null)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-loginlimit-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.LoginAttemptsPerMinute = 3;
            o.LoginLimitIncludesLoopback = limitLoopback;
            configure?.Invoke(o);
        }, new FrozenClock());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            if (forwardedFor != null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
            var codes = new List<HttpStatusCode>();
            for (int i = 0; i < attempts; i++)
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = "limited", ["credential"] = "wrong" + i });
                codes.Add((await client.PostAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.LoginPath}", form)).StatusCode);
            }
            return codes;
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PastTheBudgetAnAddressGets429WithoutAHash()
    {
        var codes = await Logins(limitLoopback: true, attempts: 5);
        Assert.All(codes.Take(3), c => Assert.Equal(HttpStatusCode.OK, c));
        Assert.All(codes.Skip(3), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    [Fact]
    public async Task LoopbackIsExemptByDefault()
    {
        Assert.All(await Logins(limitLoopback: false, attempts: 6), c => Assert.Equal(HttpStatusCode.OK, c));
    }

    /// <summary>
    /// A proxy on the same machine connects from loopback too. A forwarded request is a player behind it, not a local
    /// tool, so it gets the budget: an untrusted proxy's players share the proxy's address, a trusted one's their own.
    /// </summary>
    [Fact]
    public async Task AForwardedRequestFromLoopbackIsLimited()
    {
        var codes = await Logins(limitLoopback: false, attempts: 5, forwardedFor: "203.0.113.7");
        Assert.All(codes.Take(3), c => Assert.Equal(HttpStatusCode.OK, c));
        Assert.All(codes.Skip(3), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    [Fact]
    public async Task BehindATrustedProxyTheBudgetIsTheForwardedClients()
    {
        var codes = await Logins(limitLoopback: false, attempts: 4, forwardedFor: "203.0.113.8",
            configure: o => o.Panel.TrustedProxies = ["127.0.0.1"]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    [Fact]
    public async Task ATunnelEndsTheLoopbackExemption()
    {
        // frp or ssh -R: strangers arrive on loopback with no header, but the client is told a public address.
        var codes = await Logins(limitLoopback: false, attempts: 4, configure: o => { o.PublicHost = "192.0.2.10"; o.BindAddress = "127.0.0.1"; });
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    [Fact]
    public async Task AConfiguredProxyEndsTheLoopbackExemption()
    {
        // A proxy that sends no forwarding header still announces itself in the panel options.
        var codes = await Logins(limitLoopback: false, attempts: 4, configure: o => o.Panel.PublicUrl = "https://panel.example.com");
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    [Theory]
    [InlineData("127.0.0.1", false, false, false)]
    [InlineData("0.0.0.0", false, false, true)]
    [InlineData("127.0.0.1", true, false, true)]
    [InlineData("127.0.0.1", false, true, true)]
    public void ThePanelIsPublicFacingOnAPublicBindOrBehindAProxy(string bind, bool trustedProxy, bool publicUrl, bool expected)
    {
        var panel = new Ro3Emu.Panel.PanelOptions
        {
            TrustedProxies = trustedProxy ? ["127.0.0.1"] : [],
            PublicUrl = publicUrl ? "https://panel.example.com" : null,
        };
        Assert.Equal(expected, panel.PublicFacing(IPAddress.Parse(bind)));
    }
}
