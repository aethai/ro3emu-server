using System.Collections.Concurrent;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    private const int ExplorationActivity = 111, ConchInteraction = 7000006;
    private const string ConchDeadline = "exploration-conch";
    private const long ConchUidBase = 3L << 42;
    private readonly ConcurrentDictionary<long, IReadOnlyDictionary<long, SpawnPoint>> _conchMaps = new();

    // The same editor point on different maps has a different wire UID. The client stores UIDs as doubles.
    internal static long ConchUid(long map, int point) => ConchUidBase | map << 20 | (uint)point;

    private IReadOnlyDictionary<long, SpawnPoint> Conches(long map) => _conchMaps.GetOrAdd(map, id =>
    {
        var result = new Dictionary<long, SpawnPoint>();
        if (id is <= 0 or >= (1 << 20) || _worldData.Map(id) is not { } world) return result;
        foreach (var group in world.SpawnGroups)
            foreach (var point in group.Points)
            {
                // NPCConfig 8000002..4 use ordinary collection. Pillars have a separate rhythm interaction.
                if (point.Id is <= 0 or >= (1 << 20) || point.ConfigId <= 0) continue;
                if (NPCConfig.Get(_data, point.ConfigId) is { NPCType: 2, NPCSubType: 12, CAward: > 0 } npc
                    && npc.InteractId is [ConchInteraction] && npc.CTimes is [0, 1])
                    result.TryAdd(ConchUid(id, point.Id), point);
            }
        return result;
    });

    private bool ConchMapOpen(GameSession session) =>
        !session.SceneLoading && session.InstanceId == 0 && session.ActivitiesReady &&
        ExplorePhaseConfig.All(_data).Any(p => p.Activityid == ExplorationActivity && p.Map.Contains(session.SceneEntry)
            && WorldPhaseOpen(session, p.Row)) && _functions.IsOpen(session, 91) && ActivityError(session, ExplorationActivity, 14) == 0;

    private Task ReloadConchesAsync(GameSession session)
    {
        session.VisibleConches.Clear();
        return SyncConchesAsync(session);
    }

    private async Task LeaveConchesAsync(GameSession session)
    {
        session.VisibleConches.Clear();
        await InterruptConchAsync(session);
    }

    private async Task SyncConchesAsync(GameSession session)
    {
        if (session.SceneLoading) return;
        bool open = ConchMapOpen(session);
        if (!open && session.VisibleConches.Count == 0) return;
        var view = new AoIViewChangeData();
        var visible = new HashSet<long>();
        if (open)
        {
            var state = session.NewActivities!.ActivityInfo[ExplorationActivity].ActivityParam.WorldExploration;
            var collected = state.MapCollect.GetValueOrDefault(session.SceneEntry);
            foreach (var (uid, point) in Conches(session.SceneEntry))
            {
                if (collected?.CollectCount.ContainsKey(point.Id) == true) continue;
                visible.Add(uid);
                if (session.VisibleConches.Contains(uid)) continue;
                var npc = NPCConfig.Get(_data, point.ConfigId)!.Value;
                var entity = SceneService.NpcEntity(new WorldNpc(uid, session.SceneEntry, WorldNpcKind.Npc,
                    point.ConfigId, _data.TextOf(npc.Row, "_kName"), (int)npc.Camp, point.X, point.Y, point.Z, point.Face, default));
                view.Bases.Add(entity);
            }
        }
        foreach (long uid in session.VisibleConches)
            if (!visible.Contains(uid)) view.LeaveAoi.Add((ulong)uid);
        session.VisibleConches.Clear();
        session.VisibleConches.UnionWith(visible);
        if (view.Bases.Count + view.LeaveAoi.Count > 0) await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    private int CheckConch(GameSession session, long uid, out SpawnPoint? point)
    {
        point = null;
        if (!ConchMapOpen(session)) return ErrorCodes.ActivityClosed;
        if (!session.VisibleConches.Contains(uid) || !Conches(session.SceneEntry).TryGetValue(uid, out point))
            return ErrorCodes.NpcMissing;
        if (session.Dead) return ErrorCodes.NpcParameter;
        var npc = NPCConfig.Get(_data, point.ConfigId)!.Value;
        double dx = session.Position.X - point.X, dy = session.Position.Y - point.Y, dz = session.Position.Z - point.Z;
        double reach = npc.InteractDistance + 1.5;
        return dx * dx + dy * dy + dz * dz <= reach * reach ? 0 : ErrorCodes.NpcTooFar;
    }

    internal async Task<bool> TryConchAsync(GameSession session, NpcInteractionReq req)
    {
        if (req.NpcUid >> 40 != ConchUidBase >> 40) return false;
        var interaction = NPCInteractConfig.Get(_data, ConchInteraction);
        var play = interaction?.StatusPerformance;
        var status = interaction is { } row ? GameplayStatusConfig.Get(_data, row.StatusId) : null;
        int result = req.InteractionId != ConchInteraction || req.SceneConfigId != session.SceneEntry ? ErrorCodes.NpcParameter
            : session.PendingConch != 0 ? ErrorCodes.Cooldown
            : status?.StatusType != (int)InteractInfo.Types.EState.ExploreCollection || play is not [> 0 and <= int.MaxValue, > 0 and <= int.MaxValue] ? ErrorCodes.FeatureUnavailable
            : CheckConch(session, req.NpcUid, out _);
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Req = req, Result = result });
        if (result != 0) return true;
        var end = _clock.GetUtcNow().AddMilliseconds(play![1]);
        session.PendingConch = req.NpcUid;
        session.ConchStartPosition = session.Position.Clone();
        _deadlines.Set(session, ConchDeadline, end, FinishConchAsync, callbackOwnsScope: true);
        await session.SendGameAsync(GatheringService.StateChange, new InteractChangeNtf
        {
            Uid = session.Role!.Id,
            DataInfo = new InteractInfo
            {
                EType = InteractInfo.Types.EState.ExploreCollection, StateId = interaction!.Value.StatusId,
                ActiveObjId = req.NpcUid, InteractConfigId = ConchInteraction,
                CollectionInfo = new CollectionState
                {
                    PlayInfo = new InteractPlayInfo
                    { PlayConfigId = (int)play[0], PlayEndTimeStamp = end.ToUnixTimeMilliseconds() }
                },
            }
        });
        return true;
    }

    internal Task ConchMovedAsync(GameSession session) =>
        session.ConchStartPosition is { } start && !start.Equals(session.Position)
            ? InterruptConchAsync(session) : Task.CompletedTask;

    internal Task InterruptConchAsync(GameSession session)
    {
        if (session.PendingConch == 0) return Task.CompletedTask;
        _deadlines.Cancel(session, ConchDeadline);
        return EndConchAsync(session, interrupted: true);
    }

    private static Task EndConchAsync(GameSession session, bool interrupted)
    {
        session.PendingConch = 0;
        session.ConchStartPosition = null;
        return session.Role == null ? Task.CompletedTask : session.SendGameAsync(GatheringService.StateChange,
            new InteractChangeNtf { Uid = session.Role.Id, DataInfo = new InteractInfo(), Interrupt = interrupted });
    }

    // LC_ActivityParser_WorldExploration reads sum per map. The per-point key is inferred from NPCConfig._iCTimes [0,1].
    internal static bool RecordConch(DBWorldExploration state, long map, int point)
    {
        if (!state.MapCollect.TryGetValue(map, out var counts)) state.MapCollect[map] = counts = new();
        if (counts.CollectCount.ContainsKey(point)) return false;
        counts.CollectCount[point] = 1;
        counts.Sum++;
        return true;
    }

    private async Task FinishConchAsync(GameSession session, DateTimeOffset due, DeadlineLease lease, CancellationToken cancellation)
    {
        using var scope = _loop.Owners == null ? null : await EnterLocalWithGateAsync(session, "activity/exploration-conch");
        if (scope == null) await session.ActivityGate.WaitAsync(cancellation);
        bool success = false;
        try
        {
            if (!_deadlines.IsCurrent(session, lease) || CheckConch(session, session.PendingConch, out var point) != 0) return;
            var next = session.NewActivities!.Clone();
            var activity = next.ActivityInfo[ExplorationActivity];
            if (!RecordConch(activity.ActivityParam.WorldExploration, session.SceneEntry, point!.Id)) return;
            long award = NPCConfig.Get(_data, point.ConfigId)!.Value.CAward;
            int result = await CommitAsync(session, next, [award],
                source: $"conch/{activity.CycleStartTime}/{session.SceneEntry}/{point.Id}");
            success = result == 0;
            if (!success) await WorldCoreService.ErrorAsync(session, SceneService.NpcInteraction, result);
        }
        finally
        {
            session.ActivityGate.Release();
            if (_deadlines.IsCurrent(session, lease)) await EndConchAsync(session, !success);
        }
    }
}
