using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Maps;

public sealed partial class World
{
    /// <summary>
    /// One map's AOI dispatch. Membership is chosen under _sync while the writes await sockets, so a map serializes its
    /// player and pet transitions across both phases: a lineup refresh cannot send a pet before a simultaneous owner's
    /// 31037 enter. Maps share no AOI edges (each view is built from its own grid), so a slow send on one map no longer
    /// holds the moves of every other map.
    /// </summary>
    private sealed class SceneDispatch
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        // MoveCoreAsync runs only under its map's gate (not reentrant), so its per-walk lists are reused (they
        // were 238 MB a minute of garbage at 500 players).
        public readonly List<(GameSession Viewer, GameSession Subject, Change Kind)> MoveWork = [];
        public readonly List<GameSession> MoveEdges = [], MoveMovers = [];
    }

    /// <summary>The gates one operation holds: its map's, and on a join also the map it was indexed on before.</summary>
    private readonly struct DispatchHold(SceneDispatch dispatch, SceneDispatch? other)
    {
        public SceneDispatch Dispatch => dispatch;

        public void Release()
        {
            other?.Gate.Release();
            dispatch.Gate.Release();
        }
    }

    private SceneDispatch DispatchLocked(long scene)
    {
        if (!_dispatch.TryGetValue(scene, out var dispatch)) _dispatch[scene] = dispatch = new SceneDispatch();
        return dispatch;
    }

    /// <summary>The map a session's AOI lives on: the one it is indexed on, else the one it is in or heading to.</summary>
    private long SceneOfLocked(GameSession session) => _cells.TryGetValue(session, out var cell) ? cell.Scene : session.WorldScene;

    /// <summary>
    /// Wait for the session's map. A join also takes the map it was indexed on, since it removes the old edges there;
    /// two gates are always taken in scene order. When the session moved, or the map's dispatch was dropped while
    /// waiting, the gates are given back and taken again.
    /// </summary>
    private async Task<DispatchHold> HoldAsync(GameSession session, bool joining = false)
    {
        while (true)
        {
            long here, there;
            SceneDispatch own;
            SceneDispatch? old;
            lock (_sync)
            {
                here = SceneOfLocked(session);
                there = joining ? session.WorldScene : here;
                own = DispatchLocked(there);
                old = here == there ? null : DispatchLocked(here);
            }
            var (first, second) = old == null || there < here ? (own, old) : (old, own);
            await first.Gate.WaitAsync();
            if (second != null) await second.Gate.WaitAsync();
            var hold = new DispatchHold(own, old);
            bool same;
            lock (_sync)
                same = SceneOfLocked(session) == here && (!joining || session.WorldScene == there)
                    && _dispatch.GetValueOrDefault(there) == own && (old == null || _dispatch.GetValueOrDefault(here) == old);
            if (same) return hold;
            hold.Release();
        }
    }

    /// <summary>Wait for one map's dispatch (the rebalancing pass).</summary>
    private async Task<DispatchHold> HoldSceneAsync(long scene)
    {
        while (true)
        {
            SceneDispatch dispatch;
            lock (_sync) dispatch = DispatchLocked(scene);
            await dispatch.Gate.WaitAsync();
            bool same;
            lock (_sync) same = _dispatch.GetValueOrDefault(scene) == dispatch;
            if (same) return new DispatchHold(dispatch, null);
            dispatch.Gate.Release();
        }
    }

    /// <summary>A removed instance drops its dispatch unless an operation holds it (it is then kept; harmless).</summary>
    private void ForgetDispatch(long scene)
    {
        lock (_sync)
            if (_dispatch.TryGetValue(scene, out var dispatch) && dispatch.Gate.CurrentCount == 1) _dispatch.Remove(scene);
    }
}
