using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Ro3Emu:Pass:FreePremiumActivityIds: the novice pass (9201) opens its premium track without the 920703 item,
/// while the season passes (200/201) still need theirs.
/// </summary>
public sealed class NovicePassPremiumTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-novicepass-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
            o.Pass.FreePremiumActivityIds = [9201];
        }, new FixedClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)));
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task Reconcile(BotClient bot) => Service<NewActivityService>().ReconcileAsync(Session(bot));

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task TheNovicePassOpensBothTracksWhileTheSeasonPassStillWaitsForItsItem()
    {
        await using var bot = await InWorld("Novice");
        await Gm(bot, "!lvl 99");
        await Reconcile(bot);
        var activities = Session(bot).NewActivities!.ActivityInfo;

        Assert.True(activities[9201].ActivityParam.BattlePass.Premium);
        Assert.False(activities[200].ActivityParam.BattlePass.Premium);
        Assert.Equal(0, Session(bot).Progress!.Currency.GetValueOrDefault(920703));

        await Gm(bot, "!item 920132 2000");
        await Reconcile(bot);
        await bot.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        {
            RequestId = 1,
            ActivityId = 9201,
            BattlePassClaimReward = new BattlePassClaimRewardReq { RewardType = 1, Levels = { 1 } },
        });
        var claimed = await bot.Inbox.WaitAsync(e => e.IsGame(NewActivityService.Common), TimeSpan.FromSeconds(8));
        var res = CommonActivityRes.Parser.ParseFrom(claimed.Body);
        Assert.Equal(0, res.Result);
        Assert.Equal(new[] { 1 }, res.BattlePassClaimReward.ClaimedLevels);
        Assert.Equal(new[] { 1 }, Session(bot).NewActivities!.ActivityInfo[9201].ActivityParam.BattlePass.ClaimedPremiumLevels);
    }
}
