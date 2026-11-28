using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a skill reaches when its row's _kRange does not say it and its text does. A number the row holds stays the row's: Arm Cannon's
/// and Pile Bunker's _iTargetMax 5 stand against their "1 selected ... and up to 5 Other" (inferred).
/// </summary>
public sealed partial class CombatEffectsService
{
    private enum TextShape { AroundTarget, LineToTarget, FrontRectangle }

    private readonly record struct TextArea(TextShape Shape, double Size, double Width, int Count, bool Shuffled = false);

    /// <summary>(inferred) Focused Arrow Strike's row holds no width; Sharp Shooting's logic [3,150] does, as Arrow Flash's tuples name them.</summary>
    private const double ArrowLineWidth = 1.5;

    private static TextArea? AreaByText(JsonElement row)
    {
        var skill = new SkillConfig(row);
        string script = skill.ScriptLogic;
        var logic = skill.LogicData;
        double Desc(int i) => i < skill.DescData.Length && double.TryParse(skill.DescData[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        int rowMax = (int)Math.Max(1, skill.TargetMax);
        if (script == "BlitzBeat") return new(TextShape.AroundTarget, Desc(1), 0, 1 + (int)Desc(2)); // "and up to ${3} Other enemies within ${2} meters of the target"
        if (script.StartsWith("CartRevolution", StringComparison.Ordinal)) return new(TextShape.AroundTarget, Desc(0), 0, 1 + (int)Desc(1)); // "up to ${2} other enemies within ${1} meters of them"
        if (script.StartsWith("ArmCannon", StringComparison.Ordinal)) return new(TextShape.AroundTarget, Desc(3), 0, rowMax); // "within ${4} meters of them"
        // "at a random enemy within ${4} meters of the target ... up to ${7} times": logic [4] is the target and those three, and Secondary Damage's "+1 bounce" edits it.
        if (script == "PoisonBlade" && logic is [var blades, ..]) return new(TextShape.AroundTarget, Desc(3), 0, (int)blades, Shuffled: true);
        if (script == "RuiLiSheJi" && logic is [var count, var width, ..]) return new(TextShape.LineToTarget, Desc(0), width / 100, 1 + (int)count); // "up to ${2} enemies in a line"
        if (script.StartsWith("AimedBolt", StringComparison.Ordinal)) return new(TextShape.LineToTarget, Desc(0), ArrowLineWidth, 1 + (int)Desc(1));
        if (script == "FlameLauncher") return new(TextShape.FrontRectangle, Desc(3), Desc(2), rowMax); // "up to ${5} targets in a ${3} * ${4}-meter rectangular area in front of you"
        // Lord Knight talents: "the designated ST enemy ... and ${n} Other enemies within ${m} meters of the target" (their rows hold [0] and 1).
        if (script == "BrandishBD01") return new(TextShape.AroundTarget, Desc(1), 0, 1 + (int)Desc(4)); // Cavalry's Might
        if (script == "BoomerangBD02") return new(TextShape.AroundTarget, Desc(3), 0, 1 + (int)Desc(4)); // Explosive Spear
        if (script == "LuoxuancijiBD01") return new(TextShape.AroundTarget, Desc(4), 0, 1 + (int)Desc(5)); // Spiral Blast
        if (script == "QiaodaBD01") return new(TextShape.AroundTarget, Desc(8), 0, 1 + (int)Desc(9)); // Joint Beat Blast
        if (script == "PileBunker") return new(TextShape.AroundTarget, Desc(2), 0, rowMax); // "up to ${4} enemies within ${3} meters of them"
        // Rain of Spears "to up to ${3} enemies within a ${1}m*${2}m area in front of you" (and its Ultimate form), Sonic Burst "in a ${1}-meter
        // by ${2}-meter area in front of You": locked rows with _kRange [0]; their _kLogicData sizes are a metre larger than the text's.
        if (script is "HundredSpears" or "UltimateSpears" or "Sonicblast") return new(TextShape.FrontRectangle, Desc(1), Desc(0), rowMax);
        return null;
    }

    /// <summary>Magnus [npc, targets, ..] and Sanctuary [targets]: "to up to ${6}", and a talent's "target count +1" edits this number. 0 for any other row.</summary>
    private static int FieldTargets(JsonElement row)
    {
        var skill = new SkillConfig(row);
        return skill.ScriptLogic switch
        {
            "Magnus_Exorcismus" or "Magnus_ExorcismusBD" when skill.LogicData is [_, var targets, ..] => (int)targets,
            "Sanctuary" when skill.LogicData is [var targets, ..] => (int)targets,
            _ => 0,
        };
    }

    /// <summary>"up to ${4} enemies within a ${3}m radius": a trap's row holds _iTargetMax 1.</summary>
    private static int TrapTargets(JsonElement row) => new SkillConfig(row).DescData is [_, _, _, var count, ..]
        && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;

    /// <summary>Rolling Cutter, Evil Spirit Threat, Virus Spread and the like: _iRangeType 1 with a radius and several targets is an area, as their texts read.</summary>
    private static bool AreaDespiteRangeType(SkillConfig skill) =>
        skill.RangeType == SkillRelease.SingleTarget && skill.TargetMax > 1 && skill.Range is [> 0, ..];

    /// <summary>A text area as a cast places it: its locked target (a monster or, in an event, a player), the point it is measured
    /// around, and the facing a line or a front area takes (at the target, as the caster turns to it; without one, the cast's).</summary>
    private readonly record struct TextAim(TextArea Area, MonsterUnit? Monster, GameSession? Player, Vec3 Around, float Face, float Height);

    private TextAim? AimTextArea(GameSession s, ActiveSkillCast action, Vec3 center)
    {
        var row = action.Row;
        if (AreaByText(row) is not { } area) return null;
        var monster = FindMonster(s.WorldScene, action.Request.TargetId) is { Hp: > 0 } locked && CanHitMonster(s, locked) ? locked : null;
        var player = monster == null ? EventTarget(s, action.Request.TargetId) : null;
        var around = monster?.Pos ?? player?.Position ?? center;
        bool aimed = (monster != null || player != null) && Distance(action.Origin, around) > 0.01f;
        float face = aimed ? MonsterService.Angle(action.Origin.X, action.Origin.Z, around.X, around.Z) : action.Request.Face;
        return new(area, monster, player, around, face, new SkillConfig(row).AreaHeightRange / 100f);
    }

    /// <summary>Whether a text area without its locked target hits nobody (a single-target row needs its target).</summary>
    private static bool NeedsItsTarget(TextAim aim, JsonElement row) => aim.Area.Shape != TextShape.FrontRectangle
        && aim.Monster == null && aim.Player == null && new SkillConfig(row).RangeType == SkillRelease.SingleTarget;

    private static bool InsideTextArea(TextAim aim, Vec3 origin, Vec3 point)
    {
        if (aim.Height > 0 && MathF.Abs(point.Y - aim.Around.Y) > aim.Height) return false;
        if (aim.Area.Shape == TextShape.AroundTarget) return Distance(aim.Around, point) <= aim.Area.Size + 0.1f;
        var (forward, side) = FacingOffset(point.X - origin.X, point.Z - origin.Z, aim.Face);
        return Math.Abs(side) <= aim.Area.Width / 2 + .1 && forward >= -.5 && forward <= aim.Area.Size + .1;
    }

    private List<MonsterUnit>? EnemiesByText(GameSession s, ActiveSkillCast action, Vec3 center)
    {
        var row = action.Row;
        if (AimTextArea(s, action, center) is not { } aim) return null;
        if (NeedsItsTarget(aim, row)) return [];
        var hit = _world.Map(s.WorldScene).Units.OfType<MonsterUnit>().Where(m => m != aim.Monster && m.Hp > 0 && CanHitMonster(s, m)
            && Relation(row, false, race: m.Template.Race) && InsideTextArea(aim, action.Origin, m.Pos));
        var others = aim.Area.Shuffled ? hit.OrderBy(_ => Rolls.Proc(10000)) : hit.OrderBy(m => Distance(aim.Around, m.Pos)).ThenBy(m => m.Uid);
        return [.. (aim.Monster != null ? others.Prepend(aim.Monster) : others).Take(Math.Max(1, aim.Area.Count))];
    }

    /// <summary>The event players a text area hits, measured as for monsters (<see cref="EnemiesByText"/>). Null for any other row.</summary>
    private GameSession[]? PlayersByText(GameSession s, ActiveSkillCast action, Vec3 center)
    {
        if (AimTextArea(s, action, center) is not { } aim) return null;
        if (NeedsItsTarget(aim, action.Row)) return [];
        var hit = _sessions.All().Where(p => p != aim.Player && p.Role != null && CanPlayerHit(s, p) && InsideTextArea(aim, action.Origin, p.Position));
        var others = aim.Area.Shuffled ? hit.OrderBy(_ => Rolls.Proc(10000)) : hit.OrderBy(p => Distance(aim.Around, p.Position)).ThenBy(p => p.Role?.Id ?? 0);
        return [.. (aim.Player != null ? others.Prepend(aim.Player) : others).Take(Math.Max(1, aim.Area.Count))];
    }

    /// <summary>Adrenaline Rush: "If the target is a Blacksmith class, grants an Extra ...". Its enhanced row reads "regardless of Job Restriction".</summary>
    private const int BlacksmithOnlyBuffGroup = 16108;
    private static bool BlacksmithLine(int career) => career is 6200 or 6300 or 6400;
    private static bool JobAllowsBuff(string script, int buff, GameSession recipient) =>
        script != "AdrenalineRush" || buff / 100 != BlacksmithOnlyBuffGroup || BlacksmithLine(recipient.Role?.Career ?? 0);

    /// <summary>Venom Splasher: "Plants timed Dynamite ... After ${2} seconds, deals". The row marks the target with its buff, and the buff names the row that goes off.</summary>
    private async Task PlantDynamiteAsync(GameSession s, ActiveSkillCast action)
    {
        var skill = new SkillConfig(action.Row);
        if (FindMonster(s.WorldScene, action.Request.TargetId) is not { Hp: > 0 } target || !CanHitMonster(s, target)) return;
        await ApplyEffectsAsync(s, new ActiveSkillCast
        {
            Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration, Attachment = action.Attachment,
            Request = action.Request, Row = skill.WithDamage([], []), Origin = action.Origin, StartMs = action.StartMs, ChantMs = action.ChantMs, ActionMs = action.ActionMs,
            Operator = action.Operator,
        }, target.Pos.Clone(), chosenTargets: [target]);
        if (skill.Buff2 is not [[var buff, _, var fuse, ..], ..] || BuffConfig.Get(_data, buff) is not { } mark
            || mark.LogicData is not [var blast, ..] || SkillConfig.Get(_data, blast) is not { } explosion) return;
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(fuse), () =>
        {
            if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene || target.Hp <= 0) return Task.CompletedTask;
            var row = explosion.Row;
            var around = _world.Map(s.WorldScene).Units.OfType<MonsterUnit>()
                .Where(m => m.Hp > 0 && CanHitMonster(s, m) && Distance(target.Pos, m.Pos) <= explosion.Range.FirstOrDefault(300) / 100 + .1)
                .OrderBy(m => m == target ? 0 : 1).ThenBy(m => Distance(target.Pos, m.Pos)).Take((int)Math.Max(1, explosion.TargetMax)).ToList();
            return BlastAsync();
            async Task BlastAsync()
            {
                await CastExtraAsync(s, row, around);
                // "Inflicts Poisoned damage on the targets", its row's _kLogicData.
                if (explosion.LogicData is [var poison, ..] && SkillConfig.Get(_data, poison) is { } poisoned)
                    await CastExtraAsync(s, poisoned.Row, [.. around.Where(m => m.Hp > 0)]);
            }
        });
    }
}
