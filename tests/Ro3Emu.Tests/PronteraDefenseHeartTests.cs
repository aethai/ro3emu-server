using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Where the Battle for Prontera invaders hold (#499): the scene's defense group, not the born point.</summary>
public sealed class PronteraDefenseHeartTests
{
    private static readonly MapPoint Born = new(10, 0, 10, 0);

    [Fact]
    public void TheHeartIsAMemberOfTheDefenseGroup()
    {
        var world = new MapWorld
        {
            SceneEntry = 1,
            Born = Born,
            Points = new Dictionary<long, MapPoint> { [7] = new(40, 0, 60, 0), [8] = new(41, 0, 61, 0) },
            SceneGroups = [new SceneGroup(3, "other", [8]), new SceneGroup(4, PronteraDefenseService.HeartGroup, [7, 99])],
        };
        for (int i = 0; i < 20; i++) Assert.Equal(new MapPoint(40, 0, 60, 0), PronteraDefenseService.HeartCentre(world));
    }

    [Fact]
    public void AMapWithoutTheGroupKeepsItsBornPoint()
    {
        var world = new MapWorld { SceneEntry = 1, Born = Born, SceneGroups = [new SceneGroup(4, PronteraDefenseService.HeartGroup, [99])] };
        Assert.Equal(Born, PronteraDefenseService.HeartCentre(world));
        Assert.Equal(Born, PronteraDefenseService.HeartCentre(new MapWorld { SceneEntry = 1, Born = Born }));
    }
}
