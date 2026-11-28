using Romsg;
using Ro3Emu.Server.Maps;

using Attempt = Ro3Emu.Server.Game.DungeonService.Attempt;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Louie's shell game (3120703, #627), inferred as a whole: the client has no cutscene, StoryData, DungeonLua or NPC list
/// for the scene, so the original server moved the units. Its data still gives the pieces: Katie (400133) and the Bells
/// (400138) walk (NPCConfig _iSpeed), the scene has Katie's place behind the east Bell (27) and a mark in front of each
/// Bell (33-35). A step's <see cref="ReconstructedDungeons.Shuffle"/> walks Katie under her Bell, then swaps Bells pair by
/// pair, one straight across and the other round by the marks; only then do the rings appear and Louie asks for a guess.
/// </summary>
public sealed partial class ScriptedDungeonService
{
    /// <summary>The pause after Katie hides and between two swaps, and how long she stands at her place first, while the
    /// room comes up and Louie opens the game (inferred).</summary>
    private const int ShufflePauseMs = 300, CatLeadMs = 3000;

    private async Task StartShuffleAsync(Attempt a, ReconstructedDungeons.ScriptedStep step, ReconstructedDungeons.Shuffle shuffle)
    {
        int generation = a.StepGeneration, at = 0;
        if (shuffle.CatNpc != 0 && await CatWalksUnderAsync(a, shuffle) is { } walkMs) at = walkMs + ShufflePauseMs;
        foreach (var (from, to) in shuffle.Swaps)
        {
            StepLater(a, generation, at, () => SwapBellsAsync(a, shuffle, from, to));
            at += SwapMs(a, shuffle, from, to) + ShufflePauseMs;
        }
        StepLater(a, generation, at, async () =>
        {
            await SyncSceneActorsAsync(a, step.Actors); // the Bells stay, the rings come
            await Raids.SayAsync(a, step.React?.Speaker ?? 0, shuffle.Say);
        });
    }

    /// <summary>Katie walks from her place to the Bell she hides under, then is gone. The walk's time, or null without her.</summary>
    private async Task<int?> CatWalksUnderAsync(Attempt a, ReconstructedDungeons.Shuffle shuffle)
    {
        if (_catalog.ScenePoint(a.Plan.SceneEntry, shuffle.CatFrom) is not { } from
            || _catalog.ScenePoint(a.Plan.SceneEntry, shuffle.Hidden) is not { } under
            || NewSceneActor(a, new ReconstructedDungeons.SceneActor(shuffle.CatNpc, shuffle.CatFrom, [])) is not { } cat
            || !await _instances.AddPropAsync(a.Room, cat)) return null;
        a.SceneActors.Add(cat); // a step that ends first takes her away with its other actors (SyncSceneActorsAsync)
        int speed = CmPerSecond(shuffle.CatNpc), ms = CatLeadMs + LegMs(from, under, speed);
        StepLater(a, a.StepGeneration, CatLeadMs, () => WalkActorAsync(a, cat, [from, under], speed), whileFighting: true);
        StepLater(a, a.StepGeneration, ms, async () =>
        {
            if (!a.SceneActors.Remove(cat)) return;
            a.RetiredActors.Add(cat.Uid);
            await _instances.RemovePropAsync(a.Room, cat);
        }, whileFighting: true);
        return ms;
    }

    /// <summary>The Bells at two points trade places: the first straight across, the second round by the marks in front.</summary>
    private async Task SwapBellsAsync(Attempt a, ReconstructedDungeons.Shuffle shuffle, long from, long to)
    {
        var first = a.SceneActors.FirstOrDefault(u => u.NpcId == shuffle.Bell && u.Point == from);
        var second = a.SceneActors.FirstOrDefault(u => u.NpcId == shuffle.Bell && u.Point == to);
        if (first == null || second == null) return;
        var (straight, round) = SwapPaths(a, shuffle, from, to);
        first.Point = to;
        second.Point = from;
        await WalkActorAsync(a, first, straight, shuffle.SpeedCm);
        await WalkActorAsync(a, second, round, shuffle.SpeedCm);
    }

    private (MapPoint[] Straight, MapPoint[] Round) SwapPaths(Attempt a, ReconstructedDungeons.Shuffle shuffle, long from, long to)
    {
        MapPoint P(long point) => _catalog.ScenePoint(a.Plan.SceneEntry, point) ?? default;
        return ([P(from), P(to)], [P(to), P(shuffle.Lanes[to]), P(shuffle.Lanes[from]), P(from)]);
    }

    /// <summary>How long a swap takes: its longer walk.</summary>
    private int SwapMs(Attempt a, ReconstructedDungeons.Shuffle shuffle, long from, long to)
    {
        var (straight, round) = SwapPaths(a, shuffle, from, to);
        return Math.Max(PathMs(straight, shuffle.SpeedCm), PathMs(round, shuffle.SpeedCm));
    }

    private static int PathMs(MapPoint[] path, int speedCm) => path.Zip(path.Skip(1))
        .Sum(leg => LegMs(leg.First, leg.Second, speedCm));

    private static int LegMs(MapPoint from, MapPoint to, int speedCm) =>
        (int)Math.Ceiling(MonsterService.Distance(from.X, from.Z, to.X, to.Z) / Math.Max(0.01f, speedCm / 100f) * 1000);

    private int CmPerSecond(int npc) => _monsters.Template(npc) is { SpeedCm: > 0 } t ? t.SpeedCm : 300;

    /// <summary>Walks a scene actor along the points, leg by leg (31081, as a ride's carrier walks), and places it at the
    /// end. Returns the walk's time.</summary>
    private async Task<int> WalkActorAsync(Attempt a, SceneActorUnit unit, MapPoint[] path, int speedCm)
    {
        int at = 0;
        for (int i = 1; i < path.Length; i++)
        {
            var (from, to) = (path[i - 1], path[i]);
            int legMs = LegMs(from, to, speedCm);
            if (at == 0) await WalkLegAsync(a, unit, from, to, speedCm, legMs);
            else StepLater(a, a.StepGeneration, at, () => WalkLegAsync(a, unit, from, to, speedCm, legMs), whileFighting: true);
            at += legMs;
        }
        return at;
    }

    private async Task WalkLegAsync(Attempt a, SceneActorUnit unit, MapPoint from, MapPoint to, int speedCm, int legMs)
    {
        if (a.Settled || a.Room.Closed || unit.Removed) return;
        float face = MonsterService.Angle(from.X, from.Z, to.X, to.Z);
        (unit.X, unit.Y, unit.Z, unit.Face) = (to.X, to.Y, to.Z, face);
        await _world.MoveUnitAsync(unit, new MovementChangeNtf
        {
            Uid = unit.Uid, TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = from.X, Y = from.Y, Z = from.Z }, ServerTime = _loop.NowMs, MoveDuration = Math.Max(1, legMs),
                Face = face, MoveType = MovementType.MoveTypeWalk,
                MovementWalk = new MovementWalk { Dir = face, Speed = speedCm / 100f, Acc = 0, EndHeight = to.Y },
            },
        });
    }
}
