using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class GenesisNoteDropTests
{
    [ClientDataFact]
    public async Task SouthernMorrocHasThirteenReachablePickupsAndHonorsReplacementSceneMarkers()
    {
        const int scene = 10522, ordinary = 8000004, pillar = 8000312;
        var clock = new Clock();
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.PlayBook = 101;
            o.SeasonStart = "2026-10-05";
        }, clock);
        var seasons = host.Service<SeasonService>();
        var stage = new SeasonClock(host.Service<GameData>(), seasons.Start, 101, seasons.Zone).Build().StageTimeInfo[101002];
        long start = stage.StartTime + 2 * 86400 + 60;
        clock.Set(start);

        var map = host.Service<WorldData>().Map(scene)!;
        var born = map.Born!.Value;
        var reachable = map.Nav!.Reachable([(born.X, born.Y, born.Z)]);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, alice, "!lvl 99");
        await Gm(host, alice, "!warp 10522 165.5 153.5");
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityTemplateID == pillar));
        var pickups = view.Bases.Where(e => e.EntityTemplateID is ordinary or pillar).ToArray();
        Assert.Equal(12, pickups.Count(e => e.EntityTemplateID == ordinary));
        var music = Assert.Single(pickups, e => e.EntityTemplateID == pillar);
        Assert.Equal(13, pickups.Select(e => e.Uuid).Distinct().Count());
        Assert.All(pickups, e => Assert.True(reachable.Contains(e.MovementInfo.Pos.X, e.MovementInfo.Pos.Y, e.MovementInfo.Pos.Z)));

        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { SceneConfigId = scene, NpcUid = (long)music.Uuid, InteractionId = 7000100 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        clock.Set(start + 3);
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(music.Uuid));
        var player = host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");
        Assert.Equal(1, player.NewActivities!.ActivityInfo[111].ActivityParam.WorldExploration.MapCollect[scene].Sum);

        var marker = new SpawnPoint(60, born.X, born.Y, born.Z, 0, ordinary);
        var replacement = new MapWorld
        {
            SceneEntry = scene, Nav = map.Nav, Born = born,
            SpawnGroups = [new SpawnGroup(1, false, 1, 0, [], [marker])],
        };
        Assert.Equal(marker, Assert.Single(host.Service<NewActivityService>().ConchPlacements(replacement)));
    }

    [Fact]
    public void OldMorrocFallbackPickupsCountTowardsTheReplacementMapsTotal()
    {
        var collected = new DBWorldExplorationNpcCollectCount { Sum = 12 };
        collected.CollectCount[900001] = 1;
        Assert.False(NewActivityService.MorrocQuotaReached(collected, 13));
        collected.Sum++;
        Assert.True(NewActivityService.MorrocQuotaReached(collected, 13));
        collected.CollectCount.Clear();
        collected.CollectCount[60] = 1;
        Assert.False(NewActivityService.MorrocQuotaReached(collected, 13));
    }
}
