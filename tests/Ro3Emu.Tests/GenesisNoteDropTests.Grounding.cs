using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class GenesisNoteDropTests
{
    [ClientDataFact]
    public async Task GeffenNorthGatePillarIsVisibleAndUsableFromItsWalkableFloor()
    {
        const int scene = 10314, template = 8000307, finish = 7000100;
        var clock = new Clock();
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.PlayBook = 101;
            o.SeasonStart = "2026-10-05";
        }, clock);
        var data = host.Service<GameData>();
        var seasons = host.Service<SeasonService>();
        var stage = new SeasonClock(data, seasons.Start, 101, seasons.Zone).Build().StageTimeInfo[101002];
        clock.Set(stage.StartTime + 4 * 86400 + 60); // ExplorePhaseConfig 103 opens four days into this stage.

        var map = host.Service<WorldData>().Map(scene)!;
        var marker = map.SpawnGroups.SelectMany(g => g.Points).Single(p => p.ConfigId == template);
        var floor = Assert.IsType<float>(map.Nav!.WalkHeight(marker.X, marker.Z, marker.Y));
        Assert.True(floor - marker.Y > 10);
        long uid = NewActivityService.ConchUid(scene, marker.Id);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, alice, "!lvl 99");
        await Gm(host, alice, FormattableString.Invariant($"!warp {scene} {marker.X} {marker.Z}"));
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, v => v.Bases.Any(b => b.Uuid == (ulong)uid));
        var pillar = view.Bases.Single(b => b.Uuid == (ulong)uid);
        Assert.Equal(floor, pillar.MovementInfo.Pos.Y);
        Assert.Equal(marker.X, pillar.MovementInfo.Pos.X);
        Assert.Equal(marker.Z, pillar.MovementInfo.Pos.Z);

        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { SceneConfigId = scene, NpcUid = uid, InteractionId = finish });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
    }
}
