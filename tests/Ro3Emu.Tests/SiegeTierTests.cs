using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Stronghold tiers (SiegeTiers): the city ids the client labels "Main City" and "Prefecture @{1}", and the chain rule
/// that keeps a guild on its tier (a prefecture to attack a main city, tier N-1 to attack tier N).
/// </summary>
public sealed class SiegeTierTests
{
    [Fact]
    public void CitiesFallIntoMainCityAndTenPrefectureTiers()
    {
        Assert.Equal(SiegeTiers.MainCity, SiegeTiers.TierOf(1001));
        Assert.Equal(SiegeTiers.MainCity, SiegeTiers.TierOf(1010));
        Assert.Equal(1, SiegeTiers.TierOf(1101));
        Assert.Equal(1, SiegeTiers.TierOf(1110));
        Assert.Equal(2, SiegeTiers.TierOf(1111));
        Assert.Equal(2, SiegeTiers.TierOf(1120));
        Assert.Equal(3, SiegeTiers.TierOf(1130));
        Assert.Equal(-1, SiegeTiers.TierOf(9999));
        // A different grouping reads the same export in tiers of five.
        Assert.Equal(2, SiegeTiers.TierOf(1106, tierSize: 5));
    }

    [Fact]
    public void TheChainNeedsTheTierBelow()
    {
        // Nothing owned: any tier-1 prefecture is open, a main city and tier 2 are not.
        Assert.True(SiegeTiers.ChainSatisfied(1, []));
        Assert.False(SiegeTiers.ChainSatisfied(SiegeTiers.MainCity, []));
        Assert.False(SiegeTiers.ChainSatisfied(2, []));
        // Holding a prefecture opens the main city; holding tier 1 opens tier 2.
        Assert.True(SiegeTiers.ChainSatisfied(SiegeTiers.MainCity, [1]));
        Assert.True(SiegeTiers.ChainSatisfied(2, [1]));
        Assert.False(SiegeTiers.ChainSatisfied(3, [1]));
        Assert.True(SiegeTiers.ChainSatisfied(3, [2]));
    }

    [Fact]
    public void OnlyTierOneIsOpenWithoutTheChain()
    {
        // A main city is tier 0, the lowest number but the highest rank: it must still be checked.
        Assert.True(SiegeTiers.ChainApplies(SiegeTiers.MainCity));
        Assert.False(SiegeTiers.ChainApplies(1));
        Assert.True(SiegeTiers.ChainApplies(2));
        Assert.False(SiegeTiers.ChainApplies(-1));
    }
}
