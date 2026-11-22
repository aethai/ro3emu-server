using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The optional night look: off by default, and shown only in towns and fields (NightCycleService).</summary>
public sealed class NightCycleTests
{
    [Fact]
    public void TheNightLookIsOffByDefault() => Assert.False(new ServerOptions().NightCycle);

    [Theory]
    [InlineData(0L, 0L, true)]      // a town
    [InlineData(0L, 1L, true)]      // a field
    [InlineData(0L, 6L, false)]     // a dungeon floor or cave
    [InlineData(0L, 3L, false)]     // a dungeon map
    [InlineData(0L, 8L, false)]     // territory war
    [InlineData(42L, 1L, false)]    // any instance (a dungeon, a raid, a story scene), whatever its map
    [InlineData(42L, 0L, false)]
    public void OnlyTownsAndFieldsOutsideAnInstanceAreOutdoor(long instance, long sceneType, bool outdoor) =>
        Assert.Equal(outdoor, NightCycleService.IsOutdoor(instance, sceneType));

    [Fact]
    public void AMapWithoutAMapConfigRowIsIndoor() => Assert.False(NightCycleService.IsOutdoor(0, null));

    [Fact]
    public void TheClientIsToldWithTheTimeCommand()
    {
        Assert.Equal("time on", NightCycleService.EnableCommand);
        Assert.Equal("time place outdoor", NightCycleService.PlaceCommand(true));
        Assert.Equal("time place indoor", NightCycleService.PlaceCommand(false));
    }
}
