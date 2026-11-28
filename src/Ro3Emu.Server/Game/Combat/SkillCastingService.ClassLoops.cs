using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Counters and meters a class skill's text names and no row's cost does: Warg Strike's swings, the Guillotine's blades, the Mechanic's Firepower, Joint Beat's
/// shattered parts, Energy Release's countdown. Buffs that switch a skill's mode (SkillAttributeList type 15) reach the client by themselves.
/// </summary>
public sealed partial class SkillCastingService
{
    private const int EmpoweredWargStrikeBuff = 1333101, BladeMeter = 540002, DarkIllusionMarkGroup = 15301, CrossRipperGroup = 15302;
    private const int FirepowerPilesGroup = 16313, FirepowerGroup = 16317, FirepowerBuff = 1631701, CannonEnhanceBuff = 1632001;
    // Melody Vulcan's form (VulcanArmS1BD2/3) names its own Firepower Loaded, 1631501, whose _kLogicData 1631901 adds 2 to Arm Cannon's mode.
    private const int MelodyFirepowerGroup = 16315;
    private const int FirepowerAttacks = 3, EnergyReleaseShield = 1635101, EnergyReleaseRow = 1635201, ControlImmunity = 903201;
    private const int JointBeatGroup = 11221, ShatteredPartGroups = 11222, JetAccelerationBuff = 1634901, UltraBashGroup = 11631;

    private sealed class Swings { public int Count; }
    private readonly ConditionalWeakTable<GameSession, Swings> _wargSwings = [];

    private static bool IsArmCannon(string script) => script.StartsWith("ArmCannon", StringComparison.Ordinal);

    /// <summary>The casts that load Firepower: the _iMaxStack of the counter buff a Vulcan Arm row's _kBuff1 names (its group 16313).</summary>
    private int FirepowerCasts(SkillConfig skill)
    {
        foreach (var buff in skill.Buff1)
            if (buff is [var id, ..] && id / 100 == FirepowerPilesGroup && BuffConfig.Get(_data, id) is { } counter) return (int)counter.MaxStack;
        return 0;
    }

    /// <summary>The Firepower Loaded a Vulcan Arm or Flame Launcher row names in _kBuff1 (Vulcan Arm's own 1631701 when none).</summary>
    private static int FirepowerOf(SkillConfig skill)
    {
        foreach (var buff in skill.Buff1)
            if (buff is [var id, ..] && id / 100 is FirepowerGroup or MelodyFirepowerGroup) return (int)id;
        return FirepowerBuff;
    }

    /// <summary>The live Firepower Loaded group and the mode buff its _kLogicData names.</summary>
    private (int Group, long Enhance)? HeldFirepower(GameSession s)
    {
        foreach (var buff in s.Buffs)
            if (buff.ExpireMs > Now && buff.ConfigId / 100 is FirepowerGroup or MelodyFirepowerGroup)
                return (buff.ConfigId / 100, BuffConfig.Get(_data, buff.ConfigId)?.LogicData is [var enhance, ..] ? enhance : CannonEnhanceBuff);
        return null;
    }

    /// <summary>Vulcan Arm and Flame Launcher name Firepower Loaded in _kBuff1, and it comes "Every ${2} casts" and on Flame Launcher: <see cref="ClassLoopsOnCastAsync"/> gives it.
    /// AxeLeapAttack2's shield came with the flight; Tetra Vortex's resonance form names Elemental Attunement, which Mixed Accompaniment grants after the
    /// cast (ResonancePassives).</summary>
    internal static bool ComesFromALoop(string script, int buff) => buff / 100 is FirepowerGroup or MelodyFirepowerGroup
            && (script.StartsWith("VulcanArm", StringComparison.Ordinal) || script == "FlameLauncher")
        || script == "AxeLeapAttack2" && buff == 1634601
        || buff == ResonancePassives.ElementalAttunement && script.StartsWith("TetraVortex", StringComparison.Ordinal);

    private async Task ClassLoopsOnCastAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        var skill = new SkillConfig(action.Row);
        string script = skill.ScriptLogic;
        double Desc(int i) => i < skill.DescData.Length && double.TryParse(skill.DescData[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        switch (script)
        {
            // Warg Strike: "After casting this skill @{1} times, the next [Warg Strike] becomes [Empowered Warg Strike]", the buff that switches its mode.
            case "WolfAttack" when skill.DescData.Length >= 6:
                var swings = _wargSwings.GetOrCreateValue(s);
                if (++swings.Count >= WargStrikesToEmpower(skill)) { swings.Count = 0; await _buffs.AddUntilSpentAsync(s, EmpoweredWargStrikeBuff, s.Role!.Id); }
                break;
            case "WolfAttackPlus":
                foreach (var empowered in s.Buffs.Where(b => b.ConfigId == EmpoweredWargStrikeBuff).ToArray()) await _buffs.RemoveAsync(s, empowered);
                break;
            case "AxeLeapAttack2":
                await HighEnergyLandingAsync(s, skill, processed);
                break;
            // Aerial Bombardment: "You have [Immunity] while using the skill ... Increases MSPD by ${5}% for the skill's duration": its buff 1634901 is in no row.
            case "HighAltitudeBombing":
                await _buffs.AddAsync(s, JetAccelerationBuff, Math.Max(1, action.ActionMs), s.Role!.Id);
                await _buffs.AddAsync(s, ControlImmunity, Math.Max(1, action.ActionMs), s.Role!.Id);
                break;
            // Ultra Shield Bash: "your next ${7} Powerful Shield Bash": each spends a pile of the mode buff.
            case "Smite2":
                await _buffs.ConsumeBuffPilesAsync(s, UltraBashGroup, 1);
                break;
            // Rolling Cutter: "adds ${6} blades to Cross Ripper Slasher, up to ${7}".
            case "huixuandaoren" or "huixuandaorenS1" when processed.Count > 0:
                await _buffs.AddEnergyAsync(s, BladeMeter, (long)Desc(5));
                break;
            // Dark Illusion: "Immediately gains ${9} Blade stacks".
            case "hesehuanying" or "hesehuanyingS1":
                await _buffs.AddEnergyAsync(s, BladeMeter, (long)Desc(8));
                break;
            // Vulcan Arm: "Every ${6} casts of [Vulcan Arm] trigger [Firepower Loaded]" (Melody Vulcan's ${7}): the piles of its first buff,
            // whose _iMaxStack is that count at every level (15), where ${5} is the damage ratio (15% at Lv. 1, 35% at Lv. 5). Each form
            // grants the Firepower Loaded its _kBuff1 names, with the mode buff that one's _kLogicData holds.
            case var vulcan when vulcan.StartsWith("VulcanArm", StringComparison.Ordinal) && FirepowerCasts(skill) is var casts and > 0
                && _buffs.HasBuffPiles(s, FirepowerPilesGroup, casts) && await _buffs.ConsumeBuffPilesAsync(s, FirepowerPilesGroup, casts):
            case "FlameLauncher":
                int firepower = FirepowerOf(skill);
                long caster = s.Role!.Id;
                await _buffs.AddUntilSpentAsync(s, firepower, caster, FirepowerAttacks);
                await _buffs.AddUntilSpentAsync(s, BuffConfig.Get(_data, firepower)?.LogicData is [var enhance, ..] ? (int)enhance : CannonEnhanceBuff, caster);
                break;
        }
        if (script.StartsWith("AxeTornado", StringComparison.Ordinal)) await HighEnergyPulsesAsync(s, skill);
        // [Firepower Loaded]: "for ${9} attacks"; each spends a pile, and the last ends the mode it holds. While it lives every Arm Cannon is the
        // enhanced one (a standing mode such as Melody Cannon's is not a charge).
        if (IsArmCannon(script) && HeldFirepower(s) is { } fire && await _buffs.ConsumeBuffPilesAsync(s, fire.Group, 1)
            && !_buffs.HasBuffPiles(s, fire.Group, 1))
            foreach (var enhance in s.Buffs.Where(b => b.ConfigId == fire.Enhance).ToArray()) await _buffs.RemoveAsync(s, enhance);
        // Joint Beat (and its Blast talent row): "Each hit ... shatters the target's body parts in sequence": the buff's pile count names the
        // part, 11222 to 11225, at their first row.
        if (script is "Qiaoda" or "QiaodaBD01" && skill.Buff2 is [[_, _, var partMs, ..], ..])
            foreach (var target in processed.Where(m => m.Hp > 0))
                if (_buffs.UnitPiles(target, JointBeatGroup) is > 0 and var piles)
                    await _buffs.AddSupportAsync(target, (ShatteredPartGroups + Math.Min(piles, 4) - 1) * 100 + 1, (int)partMs, s.Role!.Id);
    }

    /// <summary>The number @{1} in Warg Strike's description list: the last value of its _iDescription.</summary>
    private static int WargStrikesToEmpower(SkillConfig skill) =>
        skill.Description is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0 && list[list.GetArrayLength() - 1].ValueKind == JsonValueKind.String
        && int.TryParse(list[list.GetArrayLength() - 1].GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int times) && times > 0 ? times : 2;

    /// <summary>Cross Ripper Slasher: "blade count (minimum @{1}) * PATK * ${2}%", "At ${5} blades, resets this skill's cooldown"; after Dark Illusion "multiplies the blade count of the
    /// next Cross Ripper Slasher by ${10}, and increases each hit's damage by ${11}%", its second ratio. Spending every blade is (inferred): no row says how many a throw takes.</summary>
    internal async Task<JsonElement> CrossRipperRowAsync(GameSession s, JsonElement row)
    {
        var skill = new SkillConfig(row);
        long blades = s.Combat.Energy.TryGetValue(BladeMeter, out var meter) ? meter.Value : 0;
        if (blades > 0) await _buffs.TrySpendEnergyAsync(s, BladeMeter, blades);
        bool marked = await _buffs.ConsumeBuffPilesAsync(s, DarkIllusionMarkGroup, 1);
        long count = Math.Max(1, blades) * (marked ? 3 : 1);
        long ratio = marked && skill.DamageParam1 is [_, var stronger, ..] ? stronger : skill.DamageParam1.FirstOrDefault();
        if (blades >= 4) await ResetCooldownAsync(s, CrossRipperGroup);
        return skill.WithDamage([ratio * count], skill.DamageParam2);
    }

    internal static ActiveSkillCast WithRow(ActiveSkillCast action, JsonElement row) => new()
    {
        Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration, Attachment = action.Attachment,
        Request = action.Request, Row = row, Origin = action.Origin, StartMs = action.StartMs, ChantMs = action.ChantMs, ActionMs = action.ActionMs, Operator = action.Operator,
    };

    /// <summary>Energy Release: "converts it into a [Shield] for ... then begins a ${1}-second countdown ... You have [Immunity] during the countdown. When the countdown ends, deal ...
    /// You then lose ${5}% of your current Max HP. This HP loss can" not kill. The row's buff is the countdown, its _kLogicData names the row that goes off.</summary>
    internal async Task EnergyReleaseAsync(GameSession s, ActiveSkillCast action)
    {
        var skill = new SkillConfig(action.Row);
        var plain = WithRow(action, skill.WithDamage([], []));
        await ApplyEffectsAsync(s, plain, action.Origin);
        var stats = _character.Stats(s);
        int countdown = skill.Buff1 is [[_, _, var ms, ..], ..] ? (int)ms : 3000;
        await _buffs.AddAsync(s, ControlImmunity, countdown, s.Role!.Id);
        int shieldMs = skill.DescData.Length > 5
            && int.TryParse(skill.DescData[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ? seconds * 1000 : 5000;
        await ApplySkillBuffAsync(stats, s, EnergyReleaseShield, shieldMs, s.Role!.Id);
        if (SkillConfig.Get(_data, EnergyReleaseRow) is not { } blast) return;
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(countdown), async () =>
        {
            if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene) return;
            var around = MonstersNear(s, s.Position, blast.Range.FirstOrDefault(1000) / 100f).Take((int)Math.Max(1, blast.TargetMax)).ToList();
            if (around.Count > 0) await CastExtraAsync(s, blast.Row, around);
            if (s.Dead || s.Role?.Id != action.OwnerId) return; // the blast can outlive its caster
            var now = _character.Stats(s);
            long loss = now[101] * (long)(skill.DescData.Length > 4 && double.TryParse(skill.DescData[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double share) ? share : 50) / 100;
            await _character.SetHpSpAsync(s, Math.Max(1, now[2000] - loss), null);
        });
    }
}
