using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>What a cleanse removes (CombatService.CleanseKinds, Cleanses). The ids here are invented.</summary>
public sealed class CleanseRulesTests
{
    private static JsonElement Skill(string script, params int[] logic) =>
        JsonSerializer.SerializeToElement(new { _kScriptLogic = script, _kLogicData = logic });

    private static BuffConfig Buff(int type, params int[] categories) =>
        new(JsonSerializer.SerializeToElement(new { _iType = type, _kBuffCategory = categories }));

    [Fact]
    public void AHealingSkillRemovesTheKindsItsDataNames()
    {
        Assert.Equal([3, 4], CombatService.CleanseKinds(Skill("Cleanse", 3, 4)));
        Assert.Equal([3, 4, 7], CombatService.CleanseKinds(Skill("Cleanse2", 3, 4, 7)));
    }

    [Fact]
    public void AntidoteAndDetoxifyRemovePoisonedWhateverTheirData()
    {
        Assert.Equal([1], CombatService.CleanseKinds(Skill("jieduji")));
        Assert.Equal([1], CombatService.CleanseKinds(Skill("PoisonBoom", 9999)));
    }

    [Fact]
    public void AnOrdinarySkillRemovesNothing() => Assert.Empty(CombatService.CleanseKinds(Skill("Heal", 3, 4)));

    [Theory]
    [InlineData(3, true)] // the status itself
    [InlineData(53, true)] // its copy at + 50
    [InlineData(82, true)] // its copy at + 79
    [InlineData(4, false)] // another status
    public void ACleanseMatchesAHostileBuffByItsFirstCategory(int kind, bool removed) =>
        Assert.Equal(removed, CombatService.Cleanses([3], Buff(2, kind, 999)));

    [Fact]
    public void ACleanseLeavesFriendlyBuffsAndLaterCategoriesAlone()
    {
        Assert.False(CombatService.Cleanses([3], Buff(1, 3)));
        Assert.False(CombatService.Cleanses([3], Buff(2, 999, 3)));
        Assert.False(CombatService.Cleanses([3], Buff(2)));
    }
}
