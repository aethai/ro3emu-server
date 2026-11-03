using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>The hits a row deals, against the total its text gives.</summary>
    [ClientDataTheory]
    [InlineData(1630601, 2, 12000)] // Spinning Axe, "2 hits ... totaling PATK * 120%"
    [InlineData(1532601, 2, 22000)] // Piercing Shadow, "a total of PATK*220%"
    [InlineData(1162421, 11, 35000)] // Earthshaker's spin, "a total of PATK * 350%"
    [InlineData(1162801, 3, 9000)] // Spear Formation - Crescent Moon, hits of PATK * 30%
    public async Task AMultiHitSkillDealsTheTotalItsTextGives(int skill, int hits, double total)
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skill)!.Value;
        var dealt = CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow));
        Assert.Equal(hits, dealt.Length);
        Assert.Equal(total, dealt.Sum(hit => new SkillConfig(hit.Row).DamageParam1[0]));
    }

    /// <summary>A skill with several attack points but one hit by its text stays one hit.</summary>
    [ClientDataTheory]
    [InlineData(1162401)] // Earthshaker's plain row
    [InlineData(1141201)] // Grand Cross: its 150 is an HP cost
    [InlineData(1110401)] // Bowling Bash: "three hits" is how one hit is shown
    public async Task AOneHitSkillStaysOneHit(int skill)
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skill)!.Value;
        Assert.Empty(CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow)));
    }

    [ClientDataTheory]
    [InlineData(1220101, 500)] // Magic Crasher: MATK
    [InlineData(1220111, 500)] // Magic Cannon: MATK
    [InlineData(1521001, 800)] // Soul Destroyer: both
    [InlineData(1524001, 800)] // Advanced Soul Destroyer: both
    [InlineData(1000001, 300)] // a physical row: ATK
    public async Task AHitsRatioMultipliesTheSourceItsTextNames(int skill, double source)
    {
        await using var alice = await InWorld("Alice");
        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), skill)!.Value;
        Assert.Equal(source, CombatService.DamageSource(row.Row, atk: 300, matk: 500, magic: false));
    }
}
