using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A move a 31112 carries the player on (a caster's dash or charge, a knockback, a mount's climb, Shadow Echo's return).
/// The client stops the unit's walk, puts it at the move's start and carries it to the end (LC_Scene_Sync._skillStartMove:
/// StopMove(true); LC_PerformEffect_Agent.SkillStartMove -> _customizeBattleAnim: _forceSyncPosition(startPos), then the
/// curve to endPos), while the server is at the end at once. RoleSessionService.WalkAsync drops the walks that predate it.
/// </summary>
internal sealed record SkillCarry(Vec3 Start, Vec3 End, long StartMs, long EndMs)
{
    /// <summary>
    /// (inferred) How long after the move ends a walk sent before it can still arrive: a round trip. The client applies the
    /// 31112 only when it gets it, so a walk it sent until then arrives up to a round trip after the move began, and so does
    /// the stop it sends when the 31112 stops its walk: LC_Movement_Smooth_Local._detectAndSyncWalk reports the unit's
    /// position, which _forceSyncPosition has just put at startPos, at most every 100 ms. One second covers a round trip of
    /// about 900 ms. The move's own time is no bound: Heavy Axe Smash's charge 1630505 takes 100 ms, Warg Hunt's 1333014
    /// 80 ms, so at a ping above that such a walk came after it and put the server back where the move began.
    /// </summary>
    internal const long LateWalkMs = 1000;

    /// <summary>
    /// Whether a walk to <paramref name="pos"/> reported at <paramref name="nowMs"/> predates the move: any walk while the
    /// client is still carried, and for <see cref="LateWalkMs"/> after it one nearer where the move began than where it
    /// ended (inferred). A walk from the landing on is taken, and a dropped one corrects nothing: the server stays at the
    /// end, which the walk check (SceneService.AcceptWalkAsync) measures the next walk from.
    /// </summary>
    internal bool Predates(Vec3 pos, long nowMs) =>
        nowMs < EndMs || nowMs < EndMs + LateWalkMs && Distance2(pos, Start) < Distance2(pos, End);

    private static float Distance2(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
