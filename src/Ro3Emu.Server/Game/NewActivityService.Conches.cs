using System.Collections.Concurrent;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    private const int ExplorationActivity = 111, ConchInteraction = 7000006, SpecialConchInteraction = 7000008;
    private const string ConchDeadline = "exploration-conch";
    private const long ConchUidBase = 3L << 42;
    private const int PillarFinishInteraction = 7000100, EnterMusicGameAction = 350;
    // NewActivityConfig._iActivityType of World Exploration (111).
    private const int WorldExplorationType = 14;
    // NPCConfig _iNPCType/_iNPCSubType of the Genesis pickups (8000002-8000004, 8000301-8000315, 8100004).
    private const int ConchNpcType = 2, ConchNpcSubType = 12;
    // LC_ConditionDefine: CheckArea, the exploration chapter's activity time and function open.
    private const int CheckAreaCondition = 4003, ChapterTimeCondition = 16001, FunctionOpenCondition = 1100;
    internal sealed record PillarAttempt(long Uid, long Cycle, DateTimeOffset End);
    private sealed record ConchTrigger(MapRegion? Area, ExplorePhaseConfig Phase, long Function);
    private sealed class ConchMap
    {
        public Dictionary<long, SpawnPoint> Points { get; } = [];
        public Dictionary<long, ConchTrigger> Triggers { get; } = [];
    }

    private readonly ConcurrentDictionary<long, ConchMap> _conchMaps = new();

    // The same editor point on different maps has a different wire UID. The client stores UIDs as doubles.
    internal static long ConchUid(long map, int point) => ConchUidBase | map << 20 | (uint)point;

    private ConchMap Conches(long map) => _conchMaps.GetOrAdd(map, id =>
    {
        var result = new ConchMap();
        if (id is <= 0 or >= (1 << 20) || _worldData.Map(id) is not { } world) return result;
        foreach (var point in ConchPlacements(world))
        {
            if (point.Id is <= 0 or >= (1 << 20) || point.ConfigId <= 0) continue;
            if (NPCConfig.Get(_data, point.ConfigId) is not { NPCType: ConchNpcType, NPCSubType: ConchNpcSubType, CAward: > 0 } npc
                || npc.CTimes is not [0, 1]) continue;
            long uid = ConchUid(id, point.Id);
            var placement = point;
            if (npc.InteractId is not [ConchInteraction])
            {
                bool special = npc.InteractId is [SpecialConchInteraction];
                if (!special && !IsMusicPillar(npc)) continue;
                if (ReadConchTrigger(world, npc) is not { } trigger || (special && trigger.Area == null)) continue;
                result.Triggers[uid] = trigger;
                // Geffen North Gate's pillar (editor point 69) is marked isOnGround but stored 14 m below the floor.
                // Use the same corrected position for the NPC shown to the player and its 3D interaction check.
                if (!special)
                    placement = point with { Y = world.RaisedGroundNpcHeight(point.Id, point.X, point.Y, point.Z) };
            }
            result.Points.TryAdd(uid, placement);
        }
        return result;
    });

    // EnterMusicGame's arguments are the song, success interaction and replay interaction.
    private bool IsMusicPillar(NPCConfig npc)
    {
        if (npc.InteractId is not [var start, PillarFinishInteraction] ||
            NPCInteractConfig.Get(_data, start)?.EXECAction is not { ValueKind: JsonValueKind.Array } actions) return false;
        foreach (var action in actions.EnumerateArray())
            if (action.ValueKind == JsonValueKind.Array && action.GetArrayLength() == 4
                && GameData.Number(action[0]) == EnterMusicGameAction && GameData.Number(action[1]) > 0
                && GameData.Number(action[2]) == PillarFinishInteraction && GameData.Number(action[3]) == start)
                return true;
        return false;
    }

    // LC_InteractCondition: CheckArea, exploration chapter time and function open.
    // Only project a conditional pickup when its complete authored show condition is understood.
    private ConchTrigger? ReadConchTrigger(MapWorld world, NPCConfig npc)
    {
        if (LCConditionList.Get(_data, npc.NpcShowCondition) is not { Type: 1, Inverse: false, SubConditionList: { } subs }
            || subs.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        MapRegion? area = null;
        ExplorePhaseConfig? phase = null;
        long function = 0;
        foreach (var sub in subs.EnumerateArray())
        {
            var leaf = new LCConditionList(sub);
            if (leaf.Type != 3 || leaf.Inverse) return null;
            switch (leaf.Data)
            {
                case [CheckAreaCondition, var scene, var region] when scene == world.SceneEntry && area == null:
                    // The client runs the same circle (LC_InteractCondition.CheckArea -> LC_AreaHelper.CheckInArea).
                    if (!world.Regions.TryGetValue(region, out area)) return null;
                    break;
                case [ChapterTimeCondition, var chapter] when phase == null:
                    if (ExplorePhaseConfig.Get(_data, chapter) is not { Activityid: ExplorationActivity } row) return null;
                    phase = row;
                    break;
                case [FunctionOpenCondition, > 0 and var id] when function == 0: function = id; break;
                default: return null;
            }
        }
        return phase.HasValue && phase.Value.Map.Contains(world.SceneEntry) && function > 0 ? new(area, phase.Value, function) : null;
    }

    private bool ConchShown(GameSession session, ConchMap map, long uid) =>
        !MorrocFallbackQuotaReached(session) &&
        (!map.Triggers.TryGetValue(uid, out var trigger) || (trigger.Area?.Contains(session.Position.X, session.Position.Z) ?? true)
            && WorldPhaseOpen(session, trigger.Phase.Row) && _functions.IsOpen(session, trigger.Function));

    private bool ConchMapOpen(GameSession session) =>
        !session.SceneLoading && session.InstanceId == 0 && session.ActivitiesReady &&
        ExplorePhaseConfig.All(_data).Any(p => p.Activityid == ExplorationActivity && p.Map.Contains(session.SceneEntry)
            && WorldPhaseOpen(session, p.Row)) && _functions.IsOpen(session, EveConstant("ExploreFunction")) && ActivityError(session, ExplorationActivity, WorldExplorationType) == 0;

    private Task ReloadConchesAsync(GameSession session)
    {
        session.PlayingPillar = null;
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
            var map = Conches(session.SceneEntry);
            foreach (var (uid, point) in map.Points)
            {
                if (collected?.CollectCount.ContainsKey(point.Id) == true || !ConchShown(session, map, uid)) continue;
                visible.Add(uid);
                if (session.VisibleConches.Contains(uid)) continue;
                var npc = NPCConfig.Get(_data, point.ConfigId)!.Value;
                var entity = SceneService.NpcEntity(new WorldNpc(uid, session.SceneEntry, WorldNpcKind.Npc,
                    point.ConfigId, npc.DisplayName(_data), (int)npc.Camp, point.X, point.Y, point.Z, point.Face, default));
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
        var map = Conches(session.SceneEntry);
        if (!session.VisibleConches.Contains(uid) || !map.Points.TryGetValue(uid, out point) || !ConchShown(session, map, uid))
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
        SpawnPoint? point = null;
        int result = req.SceneConfigId != session.SceneEntry ? ErrorCodes.NpcParameter : CheckConch(session, req.NpcUid, out point);
        var interaction = NPCInteractConfig.Get(_data, req.InteractionId);
        var play = interaction?.StatusPerformance;
        var state = interaction is { } row ? GameplayStatusConfig.Get(_data, row.StatusId)?.StatusType : null;
        bool startSong = state == (int)InteractInfo.Types.EState.ExploreCollectionMiniGame;
        if (result == 0 && point != null)
        {
            var actions = NPCConfig.Get(_data, point.ConfigId)!.Value.InteractId;
            if (!actions.Contains(req.InteractionId) || (req.InteractionId == PillarFinishInteraction && !CanFinishPillar(session, req.NpcUid)))
                result = ErrorCodes.NpcParameter;
            else if (session.PendingConch != 0) result = ErrorCodes.Cooldown;
            else if ((state != (int)InteractInfo.Types.EState.ExploreCollection && !startSong)
                || play is not [> 0 and <= int.MaxValue, > 0 and <= int.MaxValue]) result = ErrorCodes.FeatureUnavailable;
        }
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Req = req, Result = result });
        if (result != 0)
        {
            await RejectPillarAsync(session, req, result);
            return true;
        }
        var end = _clock.GetUtcNow().AddMilliseconds(play![1]);
        long cycle = session.NewActivities!.ActivityInfo[ExplorationActivity].CycleStartTime;
        if (startSong)
        {
            // The successful 32700 reply runs EnterMusicGame on the client. Closing the HUD sends no cancellation.
            session.PlayingPillar = new(req.NpcUid, cycle, end);
            return true;
        }
        session.PlayingPillar = null;
        session.ConchCycle = cycle;
        session.PendingConch = req.NpcUid;
        session.ConchStartPosition = session.Position.Clone();
        _deadlines.Set(session, ConchDeadline, end, FinishConchAsync, callbackOwnsScope: true);
        await SendConchCastAsync(session, req, interaction!.Value, (int)play[0], end);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, SkipEnabledCheck = true, Message = "Note Pillar {Point} rejected interaction {Action} on map {Map} (request map {RequestMap}): result {Result}, song pending {Pending}")]
    private partial void LogPillarRejected(int point, int action, long map, long requestMap, int result, bool pending);

    private async Task RejectPillarAsync(GameSession session, NpcInteractionReq req, int result)
    {
        if (!session.VisibleConches.Contains(req.NpcUid) || !Conches(session.SceneEntry).Points.TryGetValue(req.NpcUid, out var point)
            || NPCConfig.Get(_data, point.ConfigId)?.InteractId is not [_, PillarFinishInteraction]) return;
        if (_log.IsEnabled(LogLevel.Information))
            LogPillarRejected(point.Id, req.InteractionId, session.SceneEntry, req.SceneConfigId, result, session.PlayingPillar != null);
        if (req.InteractionId != PillarFinishInteraction || session.PendingConch != 0 || session.Dead || session.Role == null
            || req.SceneConfigId != session.SceneEntry || (session.PlayingPillar is { } song && song.Uid != req.NpcUid)
            || NPCInteractConfig.Get(_data, PillarFinishInteraction) is not { StatusPerformance: [> 0 and <= int.MaxValue and var animation, > 0 and <= int.MaxValue and var duration] } finish) return;
        // On a normal HUD finish, LC_State_Unit_Interact.StateStart schedules Exit3C using the
        // configured animation duration. An already expired cast followed immediately by idle
        // can collapse into one frame. Allow the client to enter and leave the recovery animation.
        // Its callback can only release controls; it never calls FinishConchAsync or settles loot.
        var end = _clock.GetUtcNow().AddMilliseconds(duration);
        session.PendingConch = req.NpcUid;
        _deadlines.Set(session, ConchDeadline, end, (s, _, _, _) => EndConchAsync(s, interrupted: true));
        await SendConchCastAsync(session, req, finish, (int)animation, end);
    }

    private static Task SendConchCastAsync(GameSession session, NpcInteractionReq req, NPCInteractConfig interaction,
        int animation, DateTimeOffset end) =>
        session.SendGameAsync(GatheringService.StateChange, new InteractChangeNtf
        {
            Uid = session.Role!.Id,
            DataInfo = new InteractInfo
            {
                EType = InteractInfo.Types.EState.ExploreCollection, StateId = interaction.StatusId,
                ActiveObjId = req.NpcUid, InteractConfigId = req.InteractionId,
                CollectionInfo = new CollectionState
                {
                    PlayInfo = new InteractPlayInfo
                    { PlayConfigId = animation, PlayEndTimeStamp = end.ToUnixTimeMilliseconds() }
                },
            }
        });

    // The stock HUD reports its result without notes or a start token. Live clients can finish with
    // no pending song on the server. CheckConch still validates the point, chapter, life and reach;
    // when we do have a start, also enforce its target, cycle and time limit.
    private bool CanFinishPillar(GameSession session, long uid) =>
        session.PlayingPillar is not { } song || (song.Uid == uid && song.End > _clock.GetUtcNow()
        && song.Cycle == session.NewActivities!.ActivityInfo[ExplorationActivity].CycleStartTime);

    internal async Task ConchMovedAsync(GameSession session)
    {
        // LC_DAL_Interact.OnActionReq force-syncs raw movement before the finish packet.
        // Keep the tracked start across in-range corrections. Leaving range drops it; a later finish still checks reach.
        if (session.PlayingPillar is { } song && CheckConch(session, song.Uid, out _) != 0)
            session.PlayingPillar = null;
        if (session.ConchStartPosition is { } start && !start.Equals(session.Position))
            await InterruptConchAsync(session);
        if (session.InstanceId != 0 || !_conchMaps.TryGetValue(session.SceneEntry, out var map)) return;
        foreach (var trigger in map.Triggers.Values)
        {
            var previous = session.PreviousPosition;
            if (trigger.Area is not { } area || area.Contains(previous.X, previous.Z) == area.Contains(session.Position.X, session.Position.Z)) continue;
            await SyncConchesAsync(session);
            break;
        }
    }

    internal Task InterruptConchAsync(GameSession session)
    {
        session.PlayingPillar = null;
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
            if (activity.CycleStartTime != session.ConchCycle || !RecordConch(activity.ActivityParam.WorldExploration, session.SceneEntry, point!.Id)) return;
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
