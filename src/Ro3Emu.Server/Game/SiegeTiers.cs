namespace Ro3Emu.Server.Game;

/// <summary>
/// Stronghold tiers (SiegeConfig). The client labels a city by <c>_iCityType</c> - 1 = "Main City", 2 = "Prefecture
/// @{1}" where <c>@{1}</c> is the prefecture's tier - and ships tiered settlement awards for it
/// (SiegeSettlementPrimary/Senior/HonorAward by rank).
/// <para>
/// The exported zone data describes one area only (GVGWarZoneConfig: one type-1 root and three type-2 prefectures), so a
/// city's tier is derived from its id: 1001-1099 are main cities (tier <see cref="MainCity"/>) and 1101-1199 are
/// prefectures in tiers of <see cref="TierSize"/> (1101-1110 = 1, 1111-1120 = 2, 1121-1130 = 3 in the 7 Oct 2026
/// export). (inferred: the original's tier field is not exported.)
/// </para>
/// </summary>
public static class SiegeTiers
{
    /// <summary>Main cities sit above the prefecture tiers.</summary>
    public const int MainCity = 0;

    /// <summary>Prefectures per tier when the option carries no size.</summary>
    public const int TierSize = 10;

    /// <summary>The city's tier: <see cref="MainCity"/> for a main city, 1 and up for a prefecture, -1 when not a city.</summary>
    public static int TierOf(long cityId, int tierSize = TierSize) =>
        cityId is >= 1001 and <= 1099 ? MainCity
            : cityId is >= 1101 and <= 1199 && tierSize > 0 ? 1 + (int)((cityId - 1101) / tierSize)
            : -1;

    /// <summary>
    /// The chain rule: a main city can only be challenged by a guild that holds a prefecture, and a prefecture of tier N
    /// only by a guild that holds one of tier N-1. A guild that holds nothing may still challenge any tier-1 prefecture.
    /// (inferred: the client shows the tiers but no rule; this is what makes them mean something.)
    /// </summary>
    public static bool ChainSatisfied(int cityTier, IReadOnlyCollection<int> ownedTiers)
    {
        if (cityTier < 0) return false;
        if (cityTier == MainCity) return ownedTiers.Any(tier => tier >= 1);
        return cityTier == 1 || ownedTiers.Contains(cityTier - 1);
    }

    /// <summary>
    /// Whether <see cref="ChainSatisfied"/> has anything to decide for the city: tier-1 prefectures are open to every
    /// guild, so they are not checked. A main city (tier <see cref="MainCity"/>) is checked, which is the rule that is
    /// easiest to lose by treating "tier 0" as the lowest tier.
    /// </summary>
    public static bool ChainApplies(int cityTier) => cityTier >= MainCity && cityTier != 1;
}
