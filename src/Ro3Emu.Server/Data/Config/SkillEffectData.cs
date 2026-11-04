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

    /// <summary>The damage part plays a hit reaction (hit fx, flinch, hit sound).</summary>
    public bool HasHitReaction => Damage is { ValueKind: JsonValueKind.Object } damage
        && damage.TryGetProperty("m_kActionDamge", out _);

    /// <summary>The bullet's prefab name, or null when the row flies none.</summary>
    public string? BulletName => Bullet is { } bullet && bullet.TryGetProperty("m_kBulletName", out var name)
        && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>The bullet's configured life time in ms (m_fBulletLifeTime), 0 when unset.</summary>
    public int BulletLifeTime => Bullet is { } bullet && bullet.TryGetProperty("m_fBulletLifeTime", out var life)
        && life.TryGetDouble(out double ms) && ms > 0 ? (int)ms : 0;
}
