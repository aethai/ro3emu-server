using Romsg;
using Ro3Emu.Server.Maps;
using Rule = Ro3Emu.Server.Game.ReconstructedDungeons.TrialRule;
using Who = Ro3Emu.Server.Game.ReconstructedDungeons.TrialWho;

using Attempt = Ro3Emu.Server.Game.DungeonService.Attempt;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A trial's sweeping lane objects (raid-trials.json "sweep"; Golden Thief Bug's sandstorm 4300004, guide 1050102 "a
/// sandstorm that sweeps across the entire field, damaging players in its path"). The storm is a bullet unit (NPCConfig
/// _iNPCType 10, UnitConfig 33110 "Sandstorm 40M", _fSpeed 0), which the client moves only by the server's movement sync:
/// LC_UnitManager.CreateMoveData gives a UT_Bullet the MovementBullet component (LC_Movement_BulletPathAgent), whose
/// LateUpdate runs _tickMovementSync on the MovementInfo it is sent. Inferred: it walks its lane's length in the window at
/// an even speed, and its body is a square of the lane's width.
/// </summary>
public sealed partial class RaidTrialsService
{
    /// <summary>Starts a sweeping object's walk: from the lane's start along its facing, the lane's length over the
    /// window, sent as a ride's carrier walks (ScriptedDungeonService.WalkLegAsync). Its unit keeps the lane's start, so
    /// the lane itself stays where the trial put it; how far it has got is <see cref="TrialUnit.Travelled"/>. Every
    /// player of the room shows it however far it goes (inferred: it crosses the whole field).</summary>
    private async Task StartSweepAsync(TrialRun run, WorldUnit unit, float length)
    {
        long window = Math.Max(1, run.EndsAtMs - run.StartedAtMs);
        await _world.ShowFromAfarAsync(unit);
        await _world.MoveUnitAsync(unit, new MovementChangeNtf
        {
            Uid = unit.Uid, TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = unit.X, Y = unit.Y, Z = unit.Z }, ServerTime = run.StartedAtMs,
                MoveDuration = (int)window, Face = unit.Face, MoveType = MovementType.MoveTypeWalk,
                MovementWalk = new MovementWalk { Dir = unit.Face, Speed = length * 1000 / window, Acc = 0, EndHeight = unit.Y },
            },
        });
    }

    /// <summary>On each trial tick the sweeping objects move on, and a player their body reaches is caught: the trial's
    /// failure hits that player at once, once a trial however many storms pass (inferred, as an avoid trial's verdict
    /// counts a player once). Only an avoid trial sweeps.</summary>
    private async Task TrialSweepsAsync(Attempt a, TrialRun run)
    {
        if (run.Over || run.Trial.Rule != Rule.Avoid) return;
        var sweeping = run.Objects.Where(o => o.Spec.Sweep && !o.Gone).ToArray();
        if (sweeping.Length == 0) return;
        float share = Math.Clamp((float)(_loop.NowMs - run.StartedAtMs) / Math.Max(1, run.EndsAtMs - run.StartedAtMs), 0, 1);
        foreach (var o in sweeping) o.Travelled = o.Spec.Lane is { } lane ? lane.Length * share : 0;
        var fighters = Fighters(a);
        GameSession[] caught = [.. fighters.Where(p => !run.Swept.Contains(p)
            && sweeping.Any(o => InTrialArea(o, run.Trial.Radius, p.Position.X, p.Position.Z)))];
        if (caught.Length == 0) return;
        run.Swept.UnionWith(caught);
        await TrialOutcomeAsync(a, run, run.Trial.Fail with { Who = Who.Failing }, caught, fighters, 1);
    }
}
