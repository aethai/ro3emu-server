using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

public sealed class SkillEffectDataTests
{
    // Invented EffectData rows: a reaction with only gfx, one with a flinch (m_kAni) and gfx, a bare flinch, a bullet.
    private const string Fx = """{"m_kDamage":{"m_kActionDamge":{"m_kGfxList":[{"m_kGfxName":"fx_a.prefab"}]},"m_kDamageType":3}}""";
    private const string FlinchFx = """{"m_kDamage":{"m_kActionDamge":{"m_kAni":"Hurt","m_kGfxList":[{"m_kGfxName":"fx_b.prefab"}]},"m_kDamageType":3}}""";
    private const string Flinch = """{"m_kDamage":{"m_kActionDamge":{"m_kAni":"Hurt"},"m_kDamageType":3}}""";
    private const string Bullet = """{"m_kDamage":{"m_kBulletData":{"m_kBulletName":"b"},"m_kDamageType":2}}""";

    private static Dictionary<string, JsonElement> Effects(params (long Id, string Row)[] rows) =>
        rows.ToDictionary(r => r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), r => JsonDocument.Parse(r.Row).RootElement.Clone());

    [Fact] // a hit point with no m_kAni keeps its gfx and sound: the Heal damage type stops no flinch, m_kAni does
    public void AHealKeepsAHitPointThatDoesNotFlinch()
    {
        var effects = Effects((77701, Bullet), (77702, Fx));
        Assert.Equal(77702, SkillEffectData.HealPoint(effects, 777, 77702));
    }

    [Fact] // a flinching hit point gives way to the show's first reaction without m_kAni
    public void AHealSwapsAFlinchForTheShowsReactionWithoutOne()
    {
        var effects = Effects((77701, Bullet), (77702, FlinchFx), (77703, Flinch), (77704, Fx), (77705, Fx));
        Assert.Equal(77704, SkillEffectData.HealPoint(effects, 777, 77702));
    }

    [Fact] // with no reaction free of m_kAni the heal sends none, also for the generic flinch fallback
    public void AHealWithOnlyFlinchesSendsNoPoint()
    {
        var effects = Effects((77701, Bullet), (77702, Flinch), (90001, FlinchFx));
        Assert.Equal(0, SkillEffectData.HealPoint(effects, 777, 77702));
        Assert.Equal(0, SkillEffectData.HealPoint(effects, 777, 90001));
    }

    [Fact] // the heal's own show * 1000 + 800 effect comes first, unless it flinches
    public void AHealPrefersItsOwnEffectWithoutAFlinch()
    {
        Assert.Equal(777800, SkillEffectData.HealPoint(Effects((777800, Fx), (77702, Fx)), 777, 77702));
        Assert.Equal(77702, SkillEffectData.HealPoint(Effects((777800, FlinchFx), (77702, Fx)), 777, 77702));
    }
}
