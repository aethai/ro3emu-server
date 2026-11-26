using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The Bonfire Party on the synthetic fixture: its phases follow CampFireAffixNewConfig's StageStart and End rows, and
/// the Party Blessing (action 7) pays a member in the guild home every interval up to CampfireBubbleRewardLimit,
/// reported as CampFirePlayerInfo.bubblesRewardCount. Fixture: blessing award 9307 (Guild Contribution 321) 10 s in,
/// then every 20 s; stage 2 at 120 s; End 60 s after it; limit 2.
/// </summary>
public sealed class GuildCampfireRewardTests : IAsyncLifetime
{
    private const int Contribution = 102, Blessing = 321;
    private readonly FakeTimeProvider _clock = TestServer.CreateFakeClock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartSyntheticAsync(o =>
    {
        o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
    }, _clock);

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T> => TestServer.NextAsync(bot, id, parser, match);

    private Task Advance(int seconds) => _server.AdvanceAsync(TimeSpan.FromSeconds(seconds));

    private long Contributed(BotClient bot) => _server.Service<CharacterService>().CountOf(_server.Session(bot), Contribution);

    /// <summary>A guild founder inside the guild's home.</summary>
    private async Task<BotClient> FounderAtHome()
    {
        var alice = await _server.InWorldAsync("Alice");
        await _server.GmAsync(alice, "!lvl 40");
        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Campers", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        await alice.SendGameAsync(GuildHomeService.EnterHome, new GuildEnterHomeReq());
        Assert.Equal(0, (await Next(alice, GuildHomeService.EnterHome, GuildEnterHomeResp.Parser)).Result);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == GuildCampfireService.Dungeon);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        return alice;
    }

    [Fact]
    public async Task TheBlessingPaysMembersAtHomeEachIntervalUpToTheLimitAndThePhasesFollowTheStageRows()
    {
        await using var alice = await FounderAtHome();
        long start = _clock.GetUtcNow().ToUnixTimeSeconds() + 5;
        await _server.OnLoopAsync(() => _server.Service<TimedTodoSchedule>().TestWindows[GuildCampfireService.Activity] =
            new TimedTodoSchedule.Occurrence(start, start + 600));
        long before = Contributed(alice);

        await Advance(5);
        var first = await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser);
        Assert.Equal((1, start), (first.NowStage, first.StartTime));

        await Advance(10); // the first blessing, _iDelayTime 10 s in
        await Next(alice, GuildCampfireService.PlayerInfo, CampFirePlayerInfo.Parser, p => p.BubblesRewardCount == 1);
        await Advance(10); // not yet the next _iInterval
        await Advance(10);
        await Next(alice, GuildCampfireService.PlayerInfo, CampFirePlayerInfo.Parser, p => p.BubblesRewardCount == 2);
        await _server.UntilAsync(() => Contributed(alice) == before + 2 * Blessing, "two blessings paid");
        await Advance(20); // the third tick: the limit is reached

        // Stage 2 at its StageStart row's 120 s, not at the CampFireTimeLine; the event ends with the End row 60 s later.
        await Advance(70);
        Assert.Equal(2, (await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser, r => r.NowStage == 2)).NowStage);
        await Advance(60);
        await Next(alice, GuildCampfireService.Finish, GuildCampFireFinishNotify.Parser);

        await alice.SendGameAsync(GuildCampfireService.PlayerInfo, new NullArg());
        Assert.Equal(2, (await Next(alice, GuildCampfireService.PlayerInfo, CampFirePlayerInfo.Parser,
            p => p.BubblesRewardCount != 1)).BubblesRewardCount);
        Assert.Equal(before + 2 * Blessing, Contributed(alice));
    }
}
