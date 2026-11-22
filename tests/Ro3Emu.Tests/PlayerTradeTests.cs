using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The /trade chat window (PlayerTradeService): two players swap bag instances and zeny in one market transaction, and a
/// trade whose offered item left a bag is refused whole.
/// </summary>
public sealed class PlayerTradeTests : IAsyncLifetime
{
    private const int Diamond = 1251000, Healing = 10053027;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-trade-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot() => ClientData.RepoRoot;

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem"))) { OperatorToken = Operator.Token };
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        // Chat group 4_1 is the world channel the client's own slash commands travel on.
        await bot.JoinChatAsync("4_1");
        return bot;
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    /// <summary>Sends a slash command and returns the reply text it provokes.</summary>
    private async Task<string> Slash(BotClient bot, string line, string expect)
    {
        await bot.SendChatAsync("4_1", 2, line, channelIndex: 4);
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && Romsg.BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(expect), Wait);
        return Romsg.BroadCastNotify.Parser.ParseFrom(e.Body).Content;
    }

    private Task<GameSession> Session(string account) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(Service<SessionRegistry>().All().First(s => s.Account == account && s.Role != null)));

    private async Task<long> Count(string account, int entry) =>
        (await Session(account)).Items.Where(i => i.Entry == entry).Sum(i => (long)i.Stack);

    private async Task<long> Zeny(string account) =>
        (await Session(account)).Progress!.Currency.GetValueOrDefault(PlayerTradeService.Zeny);

    /// <summary>The role's mailbox: a trade hands its items over as mail, like every other item delivery.</summary>
    private async Task<int> Letters(string account)
    {
        long role = (await Session(account)).Role!.Id;
        var json = Service<RoleStore>().LoadMailbox(role);
        return json == null ? 0 : System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(json)!.Letters.Count;
    }

    [ClientDataFact]
    public async Task TwoPlayersSwapItemsAndZenyInOneTransaction()
    {
        await using var alice = await InWorld("TradeAlice");
        await using var bob = await InWorld("TradeBob");
        await Gm("tradealice", $"!item {Diamond} 5");
        await Gm("tradebob", $"!item {Healing} 3");
        await Gm("tradebob", "!zeny 400");
        long aliceZeny = await Zeny("tradealice"), bobZeny = await Zeny("tradebob");

        Assert.Contains("Trade offered to TradeBob", await Slash(alice, "/trade TradeBob", "Trade offered"));
        await Slash(bob, "/trade yes", "Trading with TradeAlice");
        Assert.Contains("You offer 5x 1-Carat Diamond", await Slash(alice, "/trade add diamond 5", "You offer"));

        // Bob pays 200 zeny for the diamonds and puts his healing up.
        await Slash(bob, "/trade zeny 200", "You offer 200 zeny");
        await Slash(bob, "/trade add healing 3", "You offer");

        await Slash(alice, "/trade lock", "locked");
        await Slash(bob, "/trade lock", "locked");
        await Slash(alice, "/trade ok", "Confirmed");
        Assert.Contains("Trade done", await Slash(bob, "/trade ok", "Trade done"));

        // The goods leave the offered bags and arrive as mail on the other side (one letter each).
        Assert.Equal(0, await Count("tradealice", Diamond));
        Assert.Equal(0, await Count("tradealice", Healing));
        Assert.Equal(0, await Count("tradebob", Healing));
        Assert.Equal(0, await Count("tradebob", Diamond));
        Assert.True(await Letters("tradealice") >= 1, "alice received the healing by mail");
        Assert.True(await Letters("tradebob") >= 1, "bob received the diamonds by mail");
        Assert.Equal(aliceZeny + 200, await Zeny("tradealice"));
        Assert.Equal(bobZeny - 200, await Zeny("tradebob"));
    }

    [ClientDataFact]
    public async Task AnOfferThatLeftTheBagRefusesTheWholeTrade()
    {
        await using var alice = await InWorld("TradeAlice");
        await using var bob = await InWorld("TradeBob");
        await Gm("tradealice", $"!item {Diamond} 2");
        await Gm("tradebob", $"!item {Healing} 1");
        await Slash(alice, "/trade TradeBob", "Trade offered");
        await Slash(bob, "/trade yes", "Trading with TradeAlice");
        await Slash(alice, "/trade add diamond 2", "You offer");
        await Slash(bob, "/trade add healing 1", "You offer");
        await Slash(alice, "/trade lock", "locked");
        await Slash(bob, "/trade lock", "locked");
        // Alice spends the offered diamonds before she confirms: nothing may move for either side.
        await Gm("tradealice", $"!delitem {Diamond} 2");
        await Slash(alice, "/trade ok", "Confirmed");
        Assert.Contains("Trade cancelled", await Slash(bob, "/trade ok", "Trade cancelled"));

        Assert.Equal(1, await Count("tradebob", Healing));
        Assert.Equal(0, await Count("tradebob", Diamond));
        Assert.Equal(0, await Count("tradealice", Healing));
    }
}
