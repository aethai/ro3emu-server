using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The ground warnings of a boss show, worked out once per skill: the ones only the server starts (#651) and the ring each
/// wave of a ringed area takes (#652).
/// </summary>
public sealed partial class BossSkillSets
{
    /// <summary>How far a warning's end may lie from a hit and still mark it (inferred: Ring of Light's first warning ends
    /// 167 ms before its first hit, the other waves within 33 ms).</summary>
    private const int RingToleranceMs = 250;

    /// <summary>
    /// EffectData holds m_kWarningRange on 575 effects, 512 of them in no show's m_kShows; the client draws those only from
    /// an effect message (LC_PerformEffect_Mgr, WarningRange), so the server starts them. A show whose eruptions turn
    /// (one prefab placed at two or more yaws, m_kBindRotate, e.g. Rock-Shattering Strike 52035 at 0, 120, 240, 60, 180,
    /// 300) gets one warning per eruption, ending at it and turned as it is, the warnings taken in id order and reused
    /// when there are fewer (inferred); a wind-up prefab (_shifa, _start, warning) is no eruption. Any other show's
    /// warnings end at the first hit, unturned (inferred).
    /// </summary>
    private (int Effect, int StartMs, float YawDeg)[] GroundWarnings(long show, JsonElement? template, int chant, int action, int[] hits)
    {
        if (hits.Length == 0) return [];
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        var timeline = SkillShow.Timeline(template);
        var listed = timeline.Select(t => t.Effect).ToHashSet();
        var warnings = Enumerable.Range(0, 100).Select(i => show * 100 + i)
            .Where(id => !listed.Contains(id) && SkillEffectData.Find(effects, id) is { HasWarning: true })
            .Select(id => (Id: (int)id, Seconds: SkillEffectData.Find(effects, id)!.Value.WarningDuration)).ToArray();
        if (warnings.Length == 0) return [];
        var eruptions = Eruptions(effects, timeline);
        if (eruptions.Length < warnings.Length || eruptions.Length < 2)
            return [.. warnings.Select(w => (w.Id, Math.Max(0, hits[0] - (int)(w.Seconds * 1000)), 0f))];
        return [.. eruptions.Select((e, i) =>
        {
            var w = warnings[i % warnings.Length];
            int end = CombatService.ImpactOffset(template, e.Seconds, chant, action);
            return (w.Id, Math.Max(0, end - (int)(w.Seconds * 1000)), e.Yaw);
        })];
    }

    /// <summary>The instances, in timeline order, of the show's one prefab placed at two or more yaws (its last such
    /// prefab if there are more), wind-ups aside.</summary>
    private static (double Seconds, float Yaw)[] Eruptions(IReadOnlyDictionary<string, JsonElement> effects, (long Effect, double Seconds)[] timeline)
    {
        var placed = timeline
            .Select(t => (t.Seconds, Data: SkillEffectData.Find(effects, t.Effect)))
            .Where(t => t.Data is { GfxName: { Length: > 0 } name } && !WindUp(name))
            .Select(t => (t.Seconds, Name: t.Data!.Value.GfxName!, Yaw: t.Data.Value.GfxYaw)).ToArray();
        var turning = placed.GroupBy(p => p.Name).Where(g => g.Select(p => p.Yaw).Distinct().Count() >= 2)
            .OrderBy(g => g.Max(p => p.Seconds)).LastOrDefault();
        return turning == null ? [] : [.. turning.Select(p => (p.Seconds, p.Yaw))];
    }

    private static bool WindUp(string prefab) => prefab.Contains("_shifa", StringComparison.OrdinalIgnoreCase)
        || prefab.Contains("_start", StringComparison.OrdinalIgnoreCase) || prefab.Contains("warning", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// #652: a show that warns each wave around the boss on its own circle or ring (m_kShows' caster-anchored warnings of
    /// shape 1 or 5, no offset), one ending at each hit within <see cref="RingToleranceMs"/>: Ring of Light 52001 and Ring
    /// of Darkness 52015 0-8, 8-16, 16-24 m; Triple Quake 52114. The hit takes that ring (inferred: the warnings carry no
    /// hit of their own, but each ends on one). Null unless every hit has exactly one.
    /// </summary>
    private (float Inner, float Outer)[]? HitRings(JsonElement? template, int chant, int action, int[] hits)
    {
        if (hits.Length < 2) return null;
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        var rings = SkillShow.Timeline(template)
            .Select(t => (t.Seconds, Data: SkillEffectData.Find(effects, t.Effect)))
            .Where(t => t.Data is { HasWarning: true, WarningTarget: 0, HasWarningOffset: false, WarningShape: 1 or 5 })
            .Select(t =>
            {
                var w = t.Data!.Value;
                int end = CombatService.ImpactOffset(template, t.Seconds + w.WarningDuration, chant, action);
                return (End: end, Inner: w.WarningShape == 1 ? 0f : w.WarningInnerRadius, Outer: w.WarningShape == 1 ? w.WarningRadius : w.WarningOuterRadius);
            }).ToArray();
        if (rings.Length != hits.Length) return null;
        var byHit = hits.Select(h => rings.Where(r => Math.Abs(r.End - h) <= RingToleranceMs).ToArray()).ToArray();
        return byHit.All(m => m.Length == 1) && byHit.Select(m => m[0]).Distinct().Count() == hits.Length
            ? [.. byHit.Select(m => (m[0].Inner, m[0].Outer))] : null;
    }
}
