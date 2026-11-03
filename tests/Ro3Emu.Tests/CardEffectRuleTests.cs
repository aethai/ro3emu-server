using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The card effects' pure rules on invented rows, for the runs without client data. CardRulesTests plays them on the
/// real cards.</summary>
public sealed class CardEffectRuleTests
{
    private static AttributeConversionConfig Conversion(string pairs) => new(JsonDocument.Parse(
        $$"""{"_kAttribute1":{{pairs}},"_kAttribute2":[[999,1]]}""").RootElement);

    [Theory]
    [InlineData(150, 150)]
    [InlineData(-200, -200)] // a penalty stays one, unlike STR..LUK's conversions
    public void AGroupedAttributeCountsOnItsMembersWithItsSign(double value, double member)
    {
        Dictionary<int, double> raw = new() { [77] = value, [801] = 5 };
        CharacterService.AddGroupedConversion(raw, 77, Conversion("[[801,1],[802,1]]"));
        Assert.Equal(member + 5, raw[801]);
        Assert.Equal(member, raw[802]);
        Assert.Equal(value, raw[77]);
    }

    [Fact]
    public void WithoutTheGroupedAttributeItsMembersAreLeftAlone()
    {
        Dictionary<int, double> raw = new() { [801] = 5 };
        CharacterService.AddGroupedConversion(raw, 77, Conversion("[[801,1]]"));
        Assert.Equal(5, raw[801]);
        Assert.Single(raw);
    }

    [Theory]
    [InlineData(20150, 1000)]
    [InlineData(20153, 1000)]
    [InlineData(20223, 1001)]
    [InlineData(20533, 1002)]
    [InlineData(20291, 1003)]
    [InlineData(20272, 1004)]
    [InlineData(20280, 1005)]
    [InlineData(20154, 0)] // past the Orc Lady's four tiers
    [InlineData(20501, 0)] // the size cards
    [InlineData(20190, 0)]
    public void AFamilyCardsGroupNamesItsMonsterTag(long group, long tag) => Assert.Equal(tag, CardEffects.FamilyTag(group));
}
