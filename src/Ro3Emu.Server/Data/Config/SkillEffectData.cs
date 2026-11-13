using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// One row of the skill templates' Lua_Config_EffectData (data/skills/Lua_Config_EffectData.json), keyed by effect id
/// (a skill show's entries are show*100 .. show*100+99). Only the damage part the server reads: the hit reaction
/// (m_kActionDamge, what LC_DamageHelper.CalcDamageHitPoint plays) and the bullet (m_kBulletData, what
/// LC_PerformEffect_Agent.SkillCreateFixTimeBullet flies).
/// </summary>
public readonly record struct SkillEffectData(JsonElement Row)
{
    /// <summary>The effect row <paramref name="id"/> of the template table, if any.</summary>
    public static SkillEffectData? Find(IReadOnlyDictionary<string, JsonElement> effects, long id) =>
        effects.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var row) ? new SkillEffectData(row) : null;

    /// <summary>
    /// A heal's show point: the heal's own effect without the flinch. LC_DamageHelper.ShowHitDisplay flinches only on
    /// the point's m_kActionDamge.m_kAni (<see cref="Flinches"/>) and plays m_kGfxList / m_kAudioList whatever the
    /// damage type, so DamageType.Heal stops no flinch, and a point with no m_kAni keeps the heal's gfx and sound (pet
    /// heals Alice 2351103, Mavka 2348107, Yaogun 2356304). EffectData show * 1000 + 800 holds a heal's own effect
    /// (Heal 14007800, Sanctuary 14115800; an inferred family), else <paramref name="hitPoint"/> (the show's hit
    /// point) stays. A point with m_kAni gives way to the show's first hit reaction without one (Precise Treatment
    /// 2314108 to 2314110), else none, as the hit fallback 1000002 plays ReceivingDamage.
    /// </summary>
    public static long HealPoint(IReadOnlyDictionary<string, JsonElement> effects, long scriptShow, long hitPoint)
    {
        long own = scriptShow * 1000 + 800;
        if (Find(effects, own) is { HasHitReaction: true, Flinches: false }) return own;
        if (Find(effects, hitPoint) is not { Flinches: true }) return hitPoint;
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
            if (Find(effects, id) is { HasHitReaction: true, Flinches: false }) return id;
        return 0;
    }

    /// <summary>Whether a skill template (data/skills/skill_&lt;show&gt;.json) launches its own client effects
    /// (m_kClientPoints, played from the cast's timeline by LC_Skill_RunTimeContext.PlayClientEffect).</summary>
    public static bool HasClientPoints(JsonElement template) => template.TryGetProperty("m_kClientPoints", out _);

    /// <summary>How long a ground warning this effect holds is drawn (m_kPoint.m_kWarningRange.m_fDuration), or null with none.</summary>
    public static double? WarningSeconds(JsonElement row) => row.TryGetProperty("m_kPoint", out var point) && point.TryGetProperty("m_kWarningRange", out var warning)
        ? warning.TryGetProperty("m_fDuration", out var seconds) && seconds.TryGetDouble(out double value) ? value : 0 : null;

    private JsonElement? Damage => Row.TryGetProperty("m_kDamage", out var damage) ? damage : null;

    private JsonElement? Bullet => Damage is { ValueKind: JsonValueKind.Object } damage
        && damage.TryGetProperty("m_kBulletData", out var bullet) && bullet.ValueKind == JsonValueKind.Object ? bullet : null;

    /// <summary>The row has a damage part (a hit reaction, a bullet or both).</summary>
    public bool HasDamage => Damage != null;

    private JsonElement? HitReaction => Damage is { ValueKind: JsonValueKind.Object } damage
        && damage.TryGetProperty("m_kActionDamge", out var reaction) && reaction.ValueKind == JsonValueKind.Object ? reaction : null;

    /// <summary>The damage part plays a hit reaction (hit fx, flinch, hit sound).</summary>
    public bool HasHitReaction => HitReaction != null;

    /// <summary>
    /// The hit reaction makes the target flinch: LC_DamageHelper.ShowHitDisplay enters enum_State.Hit only when
    /// m_kActionDamge.m_kAni is set (EnterState(Hit, m_kAni)); its gfx and sound come from m_kGfxList / m_kAudioList
    /// whatever the hit's damage type.
    /// </summary>
    public bool Flinches => HitReaction is { } reaction && reaction.TryGetProperty("m_kAni", out _);

    /// <summary>The bullet's prefab name, or null when the row flies none.</summary>
    public string? BulletName => Bullet is { } bullet && bullet.TryGetProperty("m_kBulletName", out var name)
        && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>The bullet's configured life time in ms (m_fBulletLifeTime), 0 when unset.</summary>
    public int BulletLifeTime => Bullet is { } bullet && bullet.TryGetProperty("m_fBulletLifeTime", out var life)
        && life.TryGetDouble(out double ms) && ms > 0 ? (int)ms : 0;
}
