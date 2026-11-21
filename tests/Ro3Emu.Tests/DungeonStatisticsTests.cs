using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A cleared team boss run's result statistics. The panel asks for the run's report after the clear
/// (30110/30116/30117) and still does once the party has moved on to the next boss, where the live attempt no
/// longer exists, so the persisted report has to answer with every member's row; 30114 leaves out an id the role
/// does not own instead of discarding the whole answer.
/// </summary>
public sealed class DungeonStatisticsTests : AuctionTestsHost
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task<long> TeamRun(BotClient alice, BotClient bob, BotClient carol)
    {
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        await Party(alice, bob, carol);
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq
        {
            SceneEntry = Scene,
            InitData = new DungeonInitData
            {
                MultiDungeonInitData = new MultiDungeonInitData { MultiDungeonId = Multi, MarkId = 1 }
            },
        });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        foreach (var member in new[] { bob, carol })
        {
            await Next(member, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser, n => n.ReadyQueue.Count == 3);
            await member.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(member, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        long runId = await Load(alice);
        Assert.Equal(runId, await Load(bob));
        Assert.Equal(runId, await Load(carol));
        return runId;
    }

    [ClientDataFact]
    public async Task AClearedRunKeepsItsStatisticsAfterThePartyMovesOn()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long runId = await TeamRun(alice, bob, carol);

        await Kill(alice, await BossIn(runId));
        foreach (var bot in new[] { alice, bob, carol })
        {
            Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
            await Next(bot, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser);
        }
        // The clear left the run's report with every member's damage.
        await alice.SendGameAsync(DungeonService.StatisticsCurrent, new GetDungeonStatisticsInDungeonReq());
        var live = await Next(alice, DungeonService.StatisticsCurrent, GetDungeonStatisticsInDungeonResp.Parser);
        Assert.Equal(0, live.Result);
        Assert.Equal(runId, live.DungeonStatistics.StatisticsId);
        Assert.Equal(3, live.DungeonStatistics.SceneStatistics[runId].RoleStatistics.Count);
        Assert.All([(long)alice.Self!.Uuid, (long)bob.Self!.Uuid, (long)carol.Self!.Uuid],
            id => Assert.True(live.DungeonStatistics.SceneStatistics[runId].RoleStatistics.ContainsKey(id), $"role {id} is in the report"));
        Assert.True(live.DungeonStatistics.SceneStatistics[runId].RoleStatistics[(long)alice.Self!.Uuid].TotalDamage > 0, "the killer's damage");

        // The party continues to the next boss: the attempt is gone and the persisted report answers 30117 and 30110.
        await OnLoop(() => { Session(alice).DungeonAttempt = null; return 0; });
        await alice.SendGameAsync(DungeonService.StatisticsCurrent, new GetDungeonStatisticsInDungeonReq());
        var after = await Next(alice, DungeonService.StatisticsCurrent, GetDungeonStatisticsInDungeonResp.Parser);
        Assert.Equal(0, after.Result);
        Assert.Equal(runId, after.DungeonStatistics.StatisticsId);
        await alice.SendGameAsync(DungeonService.Statistics, new NullArg());
        var rows = await Next(alice, DungeonService.Statistics, DungeonStatisticsRes.Parser);
        Assert.Equal(0, rows.Result);
        Assert.Equal(3, rows.DungeonRolesInfo[runId].DungeonRolesInfo.Count);

        // 30114 keeps the report a request names and leaves out an id the role does not own.
        await alice.SendGameAsync(DungeonService.StatisticsHistory, new DungeonStatisticsInfoWithDungeonReq { StaisticsIds = { runId, 987654321 } });
        var history = await Next(alice, DungeonService.StatisticsHistory, DungeonStatisticsInfoWithDungeonResp.Parser);
        Assert.Equal(0, history.Result);
        Assert.Single(history.DungeonStatistics);
        Assert.Equal(runId, history.DungeonStatistics[0].StatisticsId);
    }
}
