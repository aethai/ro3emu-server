using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A build slot nothing was spent in takes the career's recommended build when the player switches to it. The client
/// renders its five build slots ("Switch Build") from this server and fills none of them itself, so before this a new
/// player's first switch landed on a page of free levels and nothing else. The recommendation is the row the client's
/// own recommendation reads: JobConfig._kProfessionID -> JobProfessConfig._iProfessionID -> JobProfessSkillConfig at the
/// role's job rank and level (Swordman 1100 builds profession 101, so job rank 2 / level 40 is row 101240).
/// A role that has already spent a point keeps its empty slots: those are the blank canvas a reset leaves.
/// </summary>
public sealed class SkillRecommendedBuildTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-recommend-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private WebApplication _app = null!;
    private string _http = "";

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private GameSession Session(BotClient bot) =>
        _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task<int> Switch(BotClient bot, int plan)
    {
        await bot.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = plan });
        return (await Next(bot, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result;
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    [ClientDataFact]
    public async Task ASwitchToAnEmptyBuildGivesARoleThatHasNotSpentAPointTheRecommendedBuild()
    {
        await using var bot = await InWorld("Newbie");
        // Swordman at the level of the report: job rank 2 / job level 40, and no skill point spent yet.
        await Gm("newbie", "!job 1100");
        await Gm("newbie", "!lvl 56 40");
        await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        var session = Session(bot);
        var skills = _app.Services.GetRequiredService<SkillService>();
        Assert.Equal(0, skills.SpentPoints(session.Progress!));

        // Build 2 is empty: switching to it hands the player JobProfessSkillConfig 101240.
        Assert.Equal(0, await Switch(bot, 1));
        Assert.Equal(1, session.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(10, session.SkillPlans.SkillAssignPlans[1].SkillAssigns[11001].SkillLevel);
        Assert.Equal(5, session.SkillPlans.SkillAssignPlans[1].SkillAssigns[11005].SkillLevel);
        Assert.Equal(10, skills.Level(session.Progress!, 11001));
        Assert.True(skills.SpentPoints(session.Progress!) > 0);

        // Build 1 was left alone, and going back to it comes back to the free levels.
        await Task.Delay(TimeSpan.FromSeconds(4)); // SchemeCD between build switches
        Assert.Equal(0, await Switch(bot, 0));
        Assert.Equal(0, session.SkillPlans.CurrentPlanIndex);
        Assert.Equal(0, skills.Level(session.Progress!, 11001));
    }

    [ClientDataFact]
    public async Task ABuildIsLeftEmptyOnceThePlayerHasSpentPointsOfTheirOwn()
    {
        await using var bot = await InWorld("Veteran");
        await Gm("veteran", "!job 1100");
        await Gm("veteran", "!lvl 56 40");
        await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        var session = Session(bot);
        var skills = _app.Services.GetRequiredService<SkillService>();

        // One point of their own: every empty build stays the blank canvas a reset leaves.
        await bot.SendGameAsync(SkillService.PlanUpdate, new SkillGrowthSkillPlanUpdateReq
        {
            Plan = 0,
            LevelUpList = { [11001] = new SkillData { SkillId = 11001, SkillLevel = 1 } }
        });
        Assert.Equal(0, (await Next(bot, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        Assert.Equal(1, skills.SpentPoints(session.Progress!));

        Assert.Equal(0, await Switch(bot, 1));
        Assert.Equal(1, session.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(0, session.SkillPlans.SkillAssignPlans[1].SkillAssigns[11001].SkillLevel);
    }

    [ClientDataFact]
    public async Task ACareerWithoutARecommendationStillSwitchesToAnEmptyBuild()
    {
        await using var bot = await InWorld("Novice");
        var session = Session(bot);
        var skills = _app.Services.GetRequiredService<SkillService>();
        Assert.Equal(0, skills.SpentPoints(session.Progress!));
        int before = skills.Level(session.Progress!, 11001);

        // A Novice recommends no build: the switch succeeds and the slot stays as empty as it was.
        Assert.Equal(0, await Switch(bot, 1));
        Assert.Equal(1, session.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(before, skills.Level(session.Progress!, 11001));
    }
}
