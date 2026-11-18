using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The climb or descent a flying mount's take-off or landing skill moves its rider by (WorldCoreService.
/// MountFlightSwitchAsync). The client flies only on its flight navmesh: once LC_UnitBase._stateSync sees
/// eObjectStateFly, LC_Move_System._refreshPlayerPathMove puts the unit in enum_NavigationMode.NavMesh, and
/// LC_Movement_Controller_NavMesh.CanArriveTo only moves it within m_fNavMesh2DYRange of that mesh, so a rider left on
/// the ground under it cannot move at all and never sends 31084. The client aims the pair at their ends itself
/// (LC_MountComponent._injectSkillPos, the 31102 targetPos): take-off at Lua_PF_NavMesh.GetNearestValidPos (the
/// flight mesh above the rider), landing at Lua_SceneSpace.SearchWalkableAround (ground). Neither show moves anybody
/// (skill_31101/31102 only play Takeoff/Land); the skill row's _kLogicData [group, delay ms, EffectData, ms] names the
/// displacement (3110121 / 3110221: m_bMovement, 1666 ms, m_fHeight +20 / -20 on a height curve), and only a 31112
/// SkillStartMoveNotify plays one (LC_PerformEffect_Agent.SkillStartMove).
/// </summary>
internal static class MountFlight
{
    /// <summary>enum_SkillMoveType.Move3D (Lua_GameDefine): _customizeBattleAnim rises endPos.y - startPos.y along the
    /// height curve instead of the row's m_fHeight, so the rider ends on the aimed point. (inferred) That the original
    /// sent Move3D: with Move (1) the row's +20 m would be added on top of a point already on the flight mesh.</summary>
    public const int Move3D = 5;

    /// <summary>(inferred) How far from the rider the client's aimed end may lie: the nearest mesh point is about
    /// straight above or below, and no client value bounds it.</summary>
    public const float Reach = 10;

    /// <summary>How far the client looks for its landing spot: LC_MountComponent._injectSkillPos aims a landing at
    /// Lua_SceneSpace.SearchWalkableAround(position, ..., 100), so the point can lie well away from a rider over trees or
    /// water.</summary>
    public const float LandingReach = 100;

    /// <summary>(inferred) How far around the rider the server looks for ground itself when the client's spot is unusable.</summary>
    private const int LandingSearch = 30;

    /// <summary>The highest a flyer may be above its floor (the bound WorldCoreService.FlyMoveAsync already used, inferred).</summary>
    public const float MaxHeight = 50;

    /// <summary>One switch's displacement: the EffectData the 31112 names, its time, how long after the cast's start
    /// it begins (_kLogicData[1]: 0 for the balloon, 1133 ms for the dragon's take-off) and where it ends.</summary>
    public readonly record struct Move(int EffectId, int MoveMs, int DelayMs, Vec3 End);

    /// <summary>
    /// The switch skill <paramref name="skillId"/>'s move from <paramref name="from"/>, null when it moves nobody (no
    /// displacement in its _kLogicData). A missing or implausible aimed point falls back to the row's own m_fHeight
    /// straight up for a take-off, and the nearest walkable ground for a landing (inferred); a landing always lands.
    /// A take-off aimed at no climb (the rider's own spot, where the server fills in a target unit's position) is one.
    /// </summary>
    public static Move? Plan(GameData data, int skillId, bool takeOff, MapWorld? map, Vec3 from, Vec3? aimed)
    {
        if (SkillConfig.Get(data, skillId)?.LogicData is not [_, var delay, var effectId, ..]
            || SkillEffectData.Find(data.SkillTemplateTable("Lua_Config_EffectData"), effectId) is not { IsMovement: true } effect) return null;
        var aim = aimed is { } a && WorldCoreService.Finite(a)
            && MonsterService.Distance(from.X, from.Z, a.X, a.Z) <= (takeOff ? Reach : LandingReach) ? a : null;
        var end = takeOff ? TakeOffEnd(map, from, aim, effect.MovementHeight) : LandingEnd(map, from, aim);
        return end == null ? null : new Move((int)effectId, Math.Max(1, effect.MovementMs), (int)Math.Max(0, delay), end);
    }

    private static Vec3 TakeOffEnd(MapWorld? map, Vec3 from, Vec3? aim, float height) =>
        aim != null && aim.Y > from.Y + 1 && map != null && Floor(map, aim) is { } floor && aim.Y >= floor - 1 && aim.Y <= floor + MaxHeight
            ? aim.Clone() : new Vec3 { X = from.X, Y = from.Y + Math.Max(0, height), Z = from.Z };

    private static Vec3? LandingEnd(MapWorld? map, Vec3 from, Vec3? aim)
    {
        if (map == null) return null;
        if (aim != null && Standable(map, aim) is { } aimed) return aimed;
        if (Standable(map, from) is { } below) return below;
        // Over trees, roofs or water nothing below is walkable: the nearest ground around, else the floor under the rider.
        var near = map.Standable(new MapPoint(from.X, Floor(map, from) ?? from.Y, from.Z, 0), LandingSearch);
        return new Vec3 { X = near.X, Y = near.Y, Z = near.Z };
    }

    private static Vec3? Standable(MapWorld map, Vec3 p)
    {
        var at = map.Grounded(new MapPoint(p.X, p.Y, p.Z, 0));
        return map.IsWalkable(at.X, at.Y, at.Z) ? new Vec3 { X = at.X, Y = at.Y, Z = at.Z } : null;
    }

    /// <summary>
    /// The height a flyer at <paramref name="p"/> is measured from: the walkable layer nearest to it, else, over a cell
    /// nobody walks on (water, a cliff, a roof: about two thirds of Prontera's fields), that cell's own recorded heights,
    /// the highest of them (inferred: the server has no copy of the client's flight mesh). Null off the map's grid; a map
    /// without a grid measures from the flyer itself.
    /// </summary>
    public static float? Floor(MapWorld map, Vec3 p)
    {
        if (map.Nav is not { } nav) return p.Y;
        if (nav.WalkHeight(p.X, p.Z, p.Y) is { } walk) return walk;
        float? top = null;
        foreach (var (_, height) in nav.Layers(p.X, p.Z)) top = top is { } t ? MathF.Max(t, height) : height;
        return top;
    }
}
