using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The Connect quests (TaskConfig type 19): 1901000 "Defeat MVP Angeling in a Party" [106012, 1, 5020001], 1904000
/// "Defeat Golden Thief Bug (Normal) in a party" [107005, 1, 4300001], and 1902000 "Auto-battle in a party for @{1}
/// minutes" [106014, 1], whose target nothing reads yet. All of them carry ActionConfig 116 UpdateSocialityScore.
/// </summary>
public sealed class ConnectQuestTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const long AngelingQuest = 1901000, ThiefBugQuest = 1904000, AutoBattleQuest = 1902000;
    private const int Angeling = 5020001, ThiefBug = 4300001;

    private sealed class Host(WebApplication app, BotClient bot, string dataDir, string http, string pem) : IAsyncDisposable
    {
        public BotClient Bot => bot;
        private readonly List<BotClient> _others = [];

        /// <summary>A second role (its own account) that joins Alice's party through the party protocol.</summary>
        public async Task JoinPartyAsync(string name)
        {
            var other = new BotClient(http, pem);
            _others.Add(other);
            await other.LoginAsync(name.ToLowerInvariant(), "pw");
            await other.EnterGateAsync();
            await other.EnterWorldAsync((await other.CreateRoleAsync(name)).RoleBaseInfo.Id);
            await bot.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
            var created = await NextAsync(bot, TeamService.TeamCreate, TeamCreateResp.Parser);
            Assert.Equal(0, created.Result);
            await bot.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)other.Self!.Uuid });
            Assert.Equal(0, (await NextAsync(bot, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            await NextAsync(other, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            await other.SendGameAsync(TeamService.TeamInviteeResp,
                new InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)bot.Self!.Uuid });
            Assert.Equal(0, (await NextAsync(other, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }

        private static async Task<T> NextAsync<T>(BotClient b, uint id, Google.Protobuf.MessageParser<T> parser)
            where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await b.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);
        public T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();

        public static async Task<Host> StartAsync()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-connect-" + Guid.NewGuid().ToString("N"));
            var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
            var app = ServerHost.Create([], o =>
            {
                o.DataDir = dataDir;
                o.GameDataDir = Path.Combine(RepoRoot(), "data");
                o.HttpPort = httpPort;
                o.MicroGatePort = microPort;
                o.GameGatePort = gamePort;
            });
            await app.StartAsync();
            var bot = new BotClient($"http://127.0.0.1:{httpPort}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await bot.LoginAsync("connectalice", "pw");
            await bot.EnterGateAsync();
            await bot.EnterWorldAsync((await bot.CreateRoleAsync("ConnectAlice")).RoleBaseInfo.Id);
            return new Host(app, bot, dataDir, $"http://127.0.0.1:{httpPort}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        }

        private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Role?.Name == "ConnectAlice");
        public Task Change(Func<GameSession, Task> change) => Service<GameLoop>().InvokeAsync(() => change(Alice));
        public Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Alice)));
        public Task Refresh() => Change(s => Service<TaskService>().RefreshAsync(s));
        public Task<TaskInfo?> State(long id) => Read(s => s.Tasks!.Infos.TryGetValue(id, out var info) ? info.Clone() : null);

        /// <summary>A committed server death of an NPC, credited to Alice the way the combat callback does.</summary>
        /// <param name="staged">Also runs RewardService's callback first, which stages the quest progress in the kill's
        /// reward settlement (the production path; OnKillAsync then only settles it).</param>
        public async Task KillAsync(int npc, long uid, bool staged = false)
        {
            var combat = Service<CombatService>();
            var callback = combat.Killed.Single(h => h.Target is TaskService);
            await Change(async s =>
            {
                var monster = new MonsterUnit
                {
                    Uid = uid, SceneEntry = s.SceneEntry, RuntimeMap = s.RuntimeMap, Dead = true,
                    Template = Service<MonsterService>().Template(npc)!,
                    Home = new SpawnPoint(0, 0, 0, 0, 0, npc), Group = new SpawnGroup(0, false, 1, 0, [], []),
                };
                foreach (var handler in combat.Dealt) handler(s, monster, 1);
                if (staged) await combat.Killed.Single(h => h.Target == Service<RewardService>())(s, monster);
                await callback(s, monster);
            });
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var other in _others) await other.DisposeAsync();
            await bot.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [ClientDataFact]
    public async Task ConnectQuestsStartExceptTheOnesWithATargetNothingReads()
    {
        await using var host = await Host.StartAsync();
        await host.Refresh();
        Assert.Equal(TaskState.Running, (await host.State(AngelingQuest))!.Status);
        Assert.Equal(TaskState.Running, (await host.State(ThiefBugQuest))!.Status);
        Assert.Null(await host.State(AutoBattleQuest)); // [106014, n]: auto-battle minutes in a party, no reader yet
    }

    [ClientDataFact]
    public async Task AKillInAPartyCountsAndOneAloneDoesNot()
    {
        await using var host = await Host.StartAsync();
        await host.Refresh();
        host.Service<TaskService>().PartySizeOf = _ => 1;
        await host.KillAsync(Angeling, 900001);
        Assert.Equal(0, (await host.State(AngelingQuest))!.CompleteNum[0]);

        host.Service<TaskService>().PartySizeOf = _ => 2;
        await host.KillAsync(ThiefBug, 900002); // another NPC than the quest's: no count
        Assert.Equal(0, (await host.State(AngelingQuest))!.CompleteNum[0]);
        await host.KillAsync(Angeling, 900003);
        Assert.Equal(1, (await host.State(AngelingQuest))!.CompleteNum[0]);
        Assert.Equal(1, (await host.State(ThiefBugQuest))!.CompleteNum[0]); // the Thief Bug kill above counted for its own quest
    }

    [ClientDataFact]
    public async Task AKillInAPartyCountsThroughTheStagedRewardSettlement()
    {
        await using var host = await Host.StartAsync();
        await host.Refresh();
        host.Service<TaskService>().PartySizeOf = _ => 1;
        await host.KillAsync(Angeling, 900011, staged: true);
        Assert.Equal(0, (await host.State(AngelingQuest))!.CompleteNum[0]);

        host.Service<TaskService>().PartySizeOf = _ => 2;
        await host.KillAsync(Angeling, 900012, staged: true);
        Assert.Equal(1, (await host.State(AngelingQuest))!.CompleteNum[0]);
    }

    [ClientDataFact]
    public async Task ARealPartyOfTwoCountsThroughTheHostsOwnHook()
    {
        await using var host = await Host.StartAsync();
        await host.Refresh();
        await host.KillAsync(Angeling, 900021, staged: true); // alone: the host's hook says 1
        Assert.Equal(0, (await host.State(AngelingQuest))!.CompleteNum[0]);

        await host.JoinPartyAsync("ConnectBob");
        await host.KillAsync(Angeling, 900022, staged: true);
        Assert.Equal(1, (await host.State(AngelingQuest))!.CompleteNum[0]);
    }

    [ClientDataFact]
    public async Task ActionSixteenDoesNotHoldTheDelivery()
    {
        await using var host = await Host.StartAsync();
        await host.Refresh();
        host.Service<TaskService>().PartySizeOf = _ => 3;
        await host.KillAsync(Angeling, 900004);
        await host.Refresh();
        if ((await host.State(AngelingQuest))!.Status == TaskState.Finish)
        {
            await host.Bot.SendGameAsync(TaskService.TaskDelivery,
                new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = AngelingQuest } } });
            Assert.Equal(0, Assert.Single(TaskDeliveryResp.Parser.ParseFrom(
                (await host.Bot.Inbox.WaitAsync(e => e.IsGame(TaskService.TaskDelivery), Wait)).Body).Data).Result);
        }
        Assert.Equal(1, await host.Read(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(AngelingQuest)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
