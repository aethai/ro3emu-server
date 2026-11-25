using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MG_NewActivity (13): configuration-driven 5013 state and 82951/82975/82976 operations. Sources:
/// LC_DAL_NewActivity and LC_ActivityServer. All claims use the
/// existing award transaction, including source state, costs, bound items and virtual EXP. ActivityGate
/// serializes source requests; CharacterService owns inventory locking. Cash-shop orders: NewActivityService.Purchases.
/// </summary>
public sealed partial class NewActivityService : Messaging.INpcInteractionHandler
{
    int Messaging.INpcInteractionHandler.Order => 100;
    async Task<bool> Messaging.INpcInteractionHandler.TryInteractAsync(GameSession session, NpcInteractionReq request)
    {
        if (await TryConchAsync(session, request)) return true;
        await InterruptConchAsync(session);
        return false;
    }
    public const uint ActivitySync = 5013, WorldExplorationSync = 5042, MonthCardSync = 5054;
    public const uint GetList = 82951, GetReward = 82975, Common = 82976, Notify = 82977;
    public const uint LevelAward = 83200, FunctionPreviewAward = 83201;
    public const uint PreSubmit = 82960, PreGetData = 82961, PreReceive = 82962, FragmentAsk = 82963,
        FragmentSend = 82964, FragmentCraft = 82965, FragmentProgress = 82966, FragmentAskFinished = 82967,
        WorldFill = 83450;

    /// <summary>Territory owns type-22 war state; this service retains the complete 5013 catalogue and calendar.</summary>
    public Func<GameSession, PlayerActivityTerritoryWar>? TerritoryWarState { get; set; }
    public SyncHook<GameSession> ProjectionChanged { get; } = new("NewActivityService.ProjectionChanged");

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly RoleStore _roles;
    private readonly SeasonService _seasons;
    private readonly ActivityClock _activityClock;
    private readonly AwardService _awards;
    private readonly TaskService _tasks;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly Deadlines _deadlines;
    private readonly LifeJobService _life;
    internal ExplorationNoteDrops NoteDrops { get; }
    private readonly PronteraDefenseService _defense;
    private readonly ILogger<NewActivityService> _log;
    private readonly HashSet<(GameSession Session, long Attachment)> _queued = [];
    private TimeProvider _clock => _loop.Time;
    internal long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    internal GameData GameData => _data;
    internal CharacterService Character => _character;
    internal FunctionService Functions => _functions;
    internal RoleStore Roles => _roles;
    internal SeasonService Seasons => _seasons;
    internal TimeProvider Clock => _clock;
    internal ActivityClock ActivityClock => _activityClock;
    internal AwardService Awards => _awards;
    internal TaskService Tasks => _tasks;
    internal SessionRegistry Sessions => _sessions;
    internal GameLoop Loop => _loop;
    internal Deadlines Deadlines => _deadlines;
    internal ILogger<NewActivityService> Log => _log;

    internal AdventureActivityService Adventures { get; private set; } = null!;
    internal ActivityPassService Passes { get; private set; } = null!;
    internal void BindAdventures(AdventureActivityService adventures) => Adventures = adventures;
    internal void BindPass(ActivityPassService passes) => Passes = passes;

    // ---- Adventure, conch, battle pass, month card, and purchase facades ----

    public const uint PassPanelMsg = ActivityPassService.PassPanelMsg;
    public const uint CreateOrder = ActivityPassService.CreateOrder, PayResult = ActivityPassService.PayResult,
        FirstRechargeAward = ActivityPassService.FirstRechargeAward, FirstRechargeUpdate = ActivityPassService.FirstRechargeUpdate;
    internal const int PassExpCurrency = ActivityPassService.PassExpCurrency,
        NovicePassExpCurrency = ActivityPassService.NovicePassExpCurrency,
        Liveness = ActivityPassService.Liveness;
    internal const long DailyTaskBase = ActivityPassService.DailyTaskBase, WeeklyTaskBase = ActivityPassService.WeeklyTaskBase;
    internal static readonly ActivityPassService.PassChallenge[] DailyChallenges = ActivityPassService.DailyChallenges;
    internal static readonly ActivityPassService.PassChallenge[] WeeklyChallenges = ActivityPassService.WeeklyChallenges;
    internal static long TaskIdOf(ActivityPassService.PassChallenge c) => ActivityPassService.TaskIdOf(c);
    internal static IReadOnlyList<ActivityPassService.PassChallenge> DailyFor(long roleId, long day) => ActivityPassService.DailyFor(roleId, day);
    internal void PassEvent(GameSession session, (ActivityPassService.PassGoal Goal, long Count)[] progress, long kills = 0,
        long dungeons = 0, long mvps = 0, long quest = 0) =>
        Passes.PassEvent(session, progress, kills, dungeons, mvps, quest);
    public Task PassPanelAsync(GameSession session) => Passes.PassPanelAsync(session);
    public Task CreateOrderAsync(GameSession session, CreateOrderReq req) => Passes.CreateOrderAsync(session, req);
    public Task PayResultAsync(GameSession session, PayResultReq req) => Passes.PayResultAsync(session, req);
    public Task FirstRechargeAwardsAsync(GameSession session) => Passes.FirstRechargeAwardsAsync(session);

    public Task PreSubmitAsync(GameSession session, NewActivityPreSubmitItemReq req) => Adventures.PreSubmitAsync(session, req);
    public Task PreGetDataAsync(GameSession session, NewActivityPreGetDataReq req) => Adventures.PreGetDataAsync(session, req);
    public Task PreReceiveAsync(GameSession session, NewActivityPreReceiveRewardReq req) => Adventures.PreReceiveAsync(session, req);
    public Task FragmentAskAsync(GameSession session, NewActivityFragmentGatherAskReq req) => Adventures.FragmentAskAsync(session, req);
    public Task FragmentAskFinishedAsync(GameSession session, NewActivityFragmentGatherAskFinishedReq req) => Adventures.FragmentAskFinishedAsync(session, req);
    public Task FragmentCraftAsync(GameSession session, NewActivityFragmentGatherCraftReq req) => Adventures.FragmentCraftAsync(session, req);
    public Task FragmentProgressAsync(GameSession session, NewActivityFragmentGatherProgressReq req) => Adventures.FragmentProgressAsync(session, req);
    public Task FragmentSendAsync(GameSession session, NewActivityFragmentGatherSendReq req) => Adventures.FragmentSendAsync(session, req);
    public Task WorldFillAsync(GameSession session, WorldExplorationFillPhaseNodeReq req) => Adventures.WorldFillAsync(session, req);
    internal long? TreasureOutcomeForTest { get => Adventures.TreasureOutcomeForTest; set => Adventures.TreasureOutcomeForTest = value; }
    internal static long ConchUid(long map, int point) => AdventureActivityService.ConchUid(map, point);
    internal Task<bool> TryConchAsync(GameSession session, NpcInteractionReq req) => Adventures.TryConchAsync(session, req);
    internal Task ConchMovedAsync(GameSession session) => Adventures.ConchMovedAsync(session);
    internal Task InterruptConchAsync(GameSession session) => Adventures.InterruptConchAsync(session);
    internal static bool RecordConch(DBWorldExploration state, long map, int point) => AdventureActivityService.RecordConch(state, map, point);
    internal IEnumerable<Maps.SpawnPoint> ConchPlacements(Maps.MapWorld world) => Adventures.ConchPlacements(world);
    internal static bool MorrocQuotaReached(DBWorldExplorationNpcCollectCount collected, long limit) =>
        AdventureActivityService.MorrocQuotaReached(collected, limit);

    private const int ExplorationActivity = AdventureActivityService.ExplorationActivity;
    private const int WorldExplorationType = AdventureActivityService.WorldExplorationType;
    private void InitializeAdventure(GameSession session, JsonElement row, PlayerActivityInfo info) => Adventures.InitializeAdventure(session, row, info);
    private Task<bool> HandleAdventureCommonAsync(GameSession session, CommonActivityReq req, CommonActivityRes res) =>
        Adventures.HandleAdventureCommonAsync(session, req, res);
    private bool UpdateChapterProgress(GameSession session, DBWorldExplorationPhase phase) => Adventures.UpdateChapterProgress(session, phase);
    private bool WorldPhaseWindow(GameSession session, JsonElement row, out long start, out long end) =>
        Adventures.WorldPhaseWindow(session, row, out start, out end);
    private Task<int> ClaimAdventureAsync(GameSession session, NewActivityGetRewardCommonReq req) => Adventures.ClaimAdventureAsync(session, req);
    private Task SettleEveExpiredAsync(GameSession session, DeadlineLease? scheduled = null) => Adventures.SettleEveExpiredAsync(session, scheduled);
    private Task SyncTreasureAsync(GameSession session, DeadlineLease lease) => Adventures.SyncTreasureAsync(session, lease);
    private Task SyncConchesAsync(GameSession session) => Adventures.SyncConchesAsync(session);
    private bool PassTurnReached(GameSession session, long activityId, DBNewActivityInfo? state) => Passes.PassTurnReached(session, activityId, state);
    private void InitializePass(GameSession session, JsonElement row, PlayerActivityInfo info) => Passes.InitializePass(session, row, info);
    private Task<bool> HandlePassCommonAsync(GameSession session, CommonActivityReq req, CommonActivityRes res) =>
        Passes.HandlePassCommonAsync(session, req, res);
    private Task ReconcilePassAsync(GameSession session) => Passes.ReconcilePassAsync(session);
    private void InitializeMonthCard(GameSession session, JsonElement row, PlayerActivityInfo info) => Passes.InitializeMonthCard(session, row, info);
    private Task<bool> HandleMonthCardCommonAsync(GameSession session, CommonActivityReq req, CommonActivityRes res) =>
        Passes.HandleMonthCardCommonAsync(session, req, res);
    private Task ReconcileMonthCardAsync(GameSession session) => Passes.ReconcileMonthCardAsync(session);
    private long StartOfLocalDay(long dayNumber) => Passes.StartOfLocalDay(dayNumber);

    public NewActivityService(GameData data, CharacterService character, FunctionService functions, RoleStore roles,
        SeasonService seasons, ActivityClock activityClock, AwardService awards, TaskService tasks,
        SessionRegistry sessions, GameLoop loop, PronteraDefenseService defense, Deadlines deadlines,
        LifeJobService life, ILogger<NewActivityService> log)
    {
        this._data = data; this._character = character; this._functions = functions; this._roles = roles;
        this._seasons = seasons; this._activityClock = activityClock; this._awards = awards; this._tasks = tasks;
        this._sessions = sessions; this._loop = loop; this._defense = defense; this._deadlines = deadlines;
        this._life = life; this._log = log;
        NoteDrops = new(data, seasons, _clock, s => ActivityError(s, ExplorationActivity, WorldExplorationType) == 0);
        life.ExplorationNotes = NoteDrops;
        life.ProjectedRestrictions.Subscribe(NoteDrops.Remaining);
        character.StateChanged.Subscribe(QueueRefresh);
        character.ProgressHandlers.Subscribe(s => { QueueRefresh(s); return Task.CompletedTask; });
        defense.Changed = s => { InvalidateProjection(s); return Task.CompletedTask; };
        seasons.ScheduleChanged.Subscribe(InvalidateAllSchedules);
        tasks.OpenActivityTaskGroups = OpenTaskGroups;
    }

    /// <summary>TimeLimitConfig 18 "refreshTaskDaily" (_iTimeLimitId 1013): a _kTaskRenew group renewed every game day.</summary>
    private const long DailyTaskRefresh = 1013;

    /// <summary>The (type, sonType) task groups (_kTaskRenew [refresh, type, sonType, ?]) of the session's open activities, whose
    /// tasks the client lists in their panels, and whether the group renews daily (TaskService.ActivityTasks.cs).</summary>
    private IReadOnlyDictionary<(int Type, int Son), bool> OpenTaskGroups(GameSession session)
    {
        var groups = new Dictionary<(int Type, int Son), bool>();
        if (!session.Activities.ActivitiesReady) return groups;
        foreach (var row in Definitions)
        {
            if (!row.TryGetProperty("_kTaskRenew", out var renew) || renew.ValueKind != JsonValueKind.Array || renew.GetArrayLength() == 0
                || ActivityError(session, GameData.Int(row, "_iID")) != 0) continue;
            foreach (var entry in renew.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 3)
                    groups[((int)(GameData.Number(entry[1]) ?? 0), (int)(GameData.Number(entry[2]) ?? 0))] =
                        GameData.Number(entry[0]) == DailyTaskRefresh;
        }
        return groups;
    }

    private IEnumerable<JsonElement> Definitions => _data.Table("NewActivityConfig").Values;
    internal int Playbook(GameSession session) => (int)(session.SeasonState?.PlayBookId ?? _seasons.PlayBook);
    private static bool InBook(JsonElement row, int book) => row.TryGetProperty("_kMultiverseArray", out var ids)
        && ids.ValueKind == JsonValueKind.Array && ids.EnumerateArray().Any(i => GameData.Number(i) is 0 || GameData.Number(i) == book);

    private bool Applies(GameSession session, JsonElement row)
    {
        if (_seasons.OpenContent.Closes(GameData.Int(row, "_iID"))) return false;
        int book = Playbook(session);
        if (InBook(row, book)) return true;
        if (row.TryGetProperty("_kMultiverseArray", out var ids)
            && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0) return false;
        // Activity 2000 (type 22) has no playbook mapping. Its existing playbook-101 fallback is inferred; the configured
        // activity window bounds that fallback so an expired territory season does not alter the master catalogue.
        return TerritoryWarState != null && book == 101 && GameData.Int(row, "_iID") == 2000 && GameData.Int(row, "_iActivityType") == 22
            && session.Role is { } role && _activityClock.ResolveActivity(row, Now, role.CreateTime, book).Contains(Now);
    }

    /// <summary>
    /// The activity's own open condition; OpenContent forces the rows whose condition is an unreachable level. A pass
    /// that follows another one (Pass II after Pass I) also waits for its turn (<see cref="PassTurnReached"/>).
    /// <paramref name="state"/>: the activity state being rebuilt, when it is not yet the session's.
    /// </summary>
    private bool Gate(GameSession session, JsonElement row, DBNewActivityInfo? state = null) =>
        PassTurnReached(session, GameData.Int(row, "_iID"), state)
        && (_seasons.OpenContent.ForcesActivity(GameData.Int(row, "_iID"))
            || _seasons.OpenContent.OpensStageActivities && StageActivities(Playbook(session)).Contains(GameData.Int(row, "_iID"))
            || _functions.ConditionMet(session, GameData.Int(row, "_kOpenCondition")));

    private readonly Dictionary<int, HashSet<long>> _stageActivities = [];

    /// <summary>The activities the playbook's season page lists by stage (MultiverseStage._kStageActivity).</summary>
    internal IReadOnlySet<long> StageActivities(int book)
    {
        lock (_stageActivities)
        {
            if (_stageActivities.TryGetValue(book, out var ids)) return ids;
            ids = [];
            foreach (var row in _data.Table("MultiverseStage").Values.Where(r => GameData.Int(r, "_iMultiverseID") == book))
                if (row.TryGetProperty("_kStageActivity", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var id in list.EnumerateArray())
                        if ((long)(GameData.Number(id) ?? 0) is var activity and > 0) ids.Add(activity);
            return _stageActivities[book] = ids;
        }
    }

    /// <summary>After season, quests and guild login, before exposing function-open panels.</summary>
    public async Task LoginAsync(GameSession session)
    {
        var lease = _deadlines.Capture(session);
        var saved = _roles.LoadNewActivities(session.Role!.Id);
        var loaded = saved == null ? new DBNewActivityInfo() : DBNewActivityInfo.Parser.ParseFrom(saved);
        long eveCycle = loaded.ActivityInfo.TryGetValue(117, out var eve) ? eve.CycleStartTime : 0;
        var ownerScope = _loop.Owners != null
            ? await EnterLocalWithGateAsync(session, "activity/login", loadedEveCycle: eveCycle,
                // Nested in the role select: the Eve key the select reserved (LoginKeys) decides, so the login
                // never asks for more than its parent holds when the lead window opens in between.
                loadedEveEnded: eve != null && eveCycle > 0
                    && _loop.Owners.CurrentCovers(EveKey(eveCycle, writes: true))) : null;
        if (ownerScope == null) await session.ActivityGate.WaitAsync();
        try
        {
            if (!_deadlines.IsCurrent(session, lease)) return;
            session.Activities.NewActivities = loaded;
            var runtime = _roles.LoadActivityRuntime(session.Role.Id);
            session.Activities.ActivityRuntime = runtime == null ? new() : JsonSerializer.Deserialize<ActivityRuntimeState>(runtime) ?? new();
            await SyncServerDayAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await SettleEveExpiredAsync(session, lease);
            if (!_deadlines.IsCurrent(session, lease)) return;
            var nextCalendar = session.Activities.NewActivities.Clone();
            RefreshCalendar(session, nextCalendar);
            await SaveCalendarAsync(session, nextCalendar, saved == null ? null : session.Activities.NewActivities.ToByteArray(),
                initializeRuntime: runtime == null);
            await ReconcilePassAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await ReconcileMonthCardAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await SyncAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            session.Activities.ActivitiesReady = true;
            await SyncConchesAsync(session);
            await _life.SyncRestrictionsAsync(session);
            _tasks.OnStateChanged(session); // the open activities' quests (TaskService.ActivityTasks.cs)
            ScheduleDeadline(session);
            PassEvent(session, []); // the day played, and any daily activity earned while the pass was not watching
        }
        finally { session.ActivityGate.Release(); ownerScope?.Dispose(); }
    }

    /// <summary>Recompute schedule/gates and distinct login days on the server's advancing clock.</summary>
    internal void RefreshCalendar(GameSession session, DBNewActivityInfo state)
    {
        int book = Playbook(session);
        long now = Now;
        foreach (var row in Definitions)
        {
            long id = GameData.Int(row, "_iID"), type = GameData.Int(row, "_iActivityType");
            if (!Applies(session, row)) continue;
            if (!state.ActivityTypeState.TryGetValue(type, out var typed)) state.ActivityTypeState[type] = typed = new();
            if (!typed.ActivityState.TryGetValue(id, out var personal))
            {
                typed.ActivityState[id] = personal = new PlayerActivityState { SelfData = new() };
                if (type == 2) personal.SelfData.SeasonPre = new();
            }
            if (!state.ActivityInfo.TryGetValue(id, out var info))
            {
                state.ActivityInfo[id] = info = new PlayerActivityInfo { ActivityParam = new(), ActivityKey = id.ToString(), Playbook = book };
                if (type == 3) info.ActivityParam.LevelAward = new();
                if (type == 4) info.ActivityParam.FunctionPreview = new();
                if (type == 8) info.ActivityParam.SevenDay = new();
            }
            personal.SelfData ??= new();
            if (type == 2) personal.SelfData.SeasonPre ??= new();
            info.ActivityParam ??= new();
            InitializeAdventure(session, row, info);
            InitializePass(session, row, info);
            InitializeMonthCard(session, row, info);
            var window = _activityClock.ResolveActivity(row, now, session.Role!.CreateTime, book);
            bool gate = Gate(session, row, state);
            bool active = window.Contains(now) && gate;
            info.State = active ? window.State : "_close";
            info.IsShielded = !gate;
            info.IsEnterComplete = active;
            info.IsExitComplete = now >= window.End;
            info.CycleStartTime = info.CurStartTime = personal.BeginTime = window.Start;
            info.CycleEndTime = info.CurEndTime = personal.EndTime = window.End;
            info.ActivityInstance = info.StateInstance = window.Start;
            info.StateTimeStamps[window.State] = window.Start;
            if (active && personal.EnterTime == 0) personal.EnterTime = now;
            if (type == 8 && active && info.ActivityParam.SevenDay is { } seven
                && (seven.LastLoginTime == 0 || _activityClock.Day(seven.LastLoginTime) < _activityClock.Day(now)))
            {
                seven.LoginDays = Math.Min(7, seven.LoginDays + 1);
                seven.LastLoginTime = now;
                personal.SelfData ??= new();
                personal.SelfData.SevenDay = seven.Clone();
            }
            if (type == 20) info.ActivityParam.PronteraDefense = _defense.BuildPlayerState(session);
        }
    }

    internal int ActivityError(GameSession session, long activityId, int expectedType = 0, bool allowInitializing = false)
    {
        if (!allowInitializing && !session.Activities.ActivitiesReady || session.Role == null || session.Progress == null || session.Activities.NewActivities == null)
            return ErrorCodes.FeatureUnavailable;
        if (_data.Row("NewActivityConfig", activityId) is not { } row || expectedType != 0 && GameData.Int(row, "_iActivityType") != expectedType) return ErrorCodes.Parameter;
        if (!Applies(session, row)) return ErrorCodes.ActivityClosed;
        if (!Gate(session, row)) return ErrorCodes.FunctionLocked;
        if (!_activityClock.ResolveActivity(row, Now, session.Role.CreateTime, Playbook(session)).Contains(Now)) return ErrorCodes.ActivityPeriod;
        return session.Activities.NewActivities.ActivityInfo.ContainsKey(activityId) ? 0 : ErrorCodes.ActivityClosed;
    }

    public async Task ListAsync(GameSession session)
    {
        await ReconcileAsync(session);
        var response = new GetActivityListResp();
        if (!session.Activities.ActivitiesReady || session.Activities.NewActivities == null) response.Result = ErrorCodes.FeatureUnavailable;
        else foreach (var row in Definitions.Where(r => Applies(session, r)))
        {
            long id = GameData.Int(row, "_iID");
            if (session.Activities.NewActivities.ActivityInfo.TryGetValue(id, out var info))
                response.ActivityList.Add(new NewActivityBaseInfo { Id = id, BeginTime = info.CycleStartTime, EndTime = info.CycleEndTime });
        }
        await session.SendGameAsync(GetList, response);
    }

    public async Task CommonAsync(GameSession session, CommonActivityReq req)
    {
        if (session.Role == null)
        {
            await session.SendGameAsync(Common, new CommonActivityRes
            { ActivityId = req.ActivityId, RequestId = req.RequestId, Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        var lease = _deadlines.Capture(session);
        bool social = session.Activities.ActivitiesReady && session.Owner != null && session.Attachment != null
            && (req.FragmentGatherSend != null || req.FragmentGatherAskFinished != null
                || req.BattlePassGift != null);
        LegacyOwnerScope? socialScope = social
            ? await EnterSocialWithGateAsync(session,
                req.FragmentGatherSend?.ToUserID ?? req.FragmentGatherAskFinished?.UserID
                    ?? req.BattlePassGift!.TargetPlayerId,
                req.FragmentGatherSend != null ? "activity/fragment-send"
                    : req.FragmentGatherAskFinished != null ? "activity/fragment-status" : "activity/pass-gift",
                req.FragmentGatherSend?.AskID ?? req.FragmentGatherAskFinished?.AskID ?? 0)
            : null;
        LegacyOwnerScope? localScope = socialScope == null && _loop.Owners != null
            ? await EnterLocalWithGateAsync(session, "activity/common", req) : null;
        if (socialScope == null && localScope == null) await session.ActivityGate.WaitAsync();
        OwnerNotices? notices = socialScope == null ? null : new OwnerNotices(socialScope.Context);
        try
        {
            if (!_deadlines.IsCurrent(session, lease)) return;
            if (session.Activities.ActivitiesReady && session.Activities.NewActivities != null)
            {
                await SettleEveExpiredAsync(session, lease);
                if (!_deadlines.IsCurrent(session, lease)) return;
                await RefreshCalendarAndSaveAsync(session);
            }
            var response = new CommonActivityRes { ActivityId = req.ActivityId, RequestId = req.RequestId, Result = ActivityError(session, req.ActivityId) };
            if (response.Result == 0)
            {
                if (req.GetRewardCommon != null)
                {
                    response.Result = req.GetRewardCommon.ActivityID != req.ActivityId ? ErrorCodes.Parameter : await ClaimCoreAsync(session, req.GetRewardCommon);
                    response.GetRewardCommon = new() { Result = response.Result };
                }
                else if (req.LevelAwardGetReward != null)
                {
                    response.Result = await ClaimSimpleAsync(session, req.ActivityId, req.LevelAwardGetReward.LevelAwardCfgId, false);
                    response.LevelAwardGetReward = new();
                }
                else if (req.FunctionPreviewGetReward != null)
                {
                    response.Result = await ClaimSimpleAsync(session, req.ActivityId, req.FunctionPreviewGetReward.FunctionPreviewCfgId, true);
                    response.FunctionPreviewGetReward = new();
                }
                else if (req.ActivityId == 118) response = await _defense.HandleAsync(session, req);
                else if (!await HandlePassCommonAsync(session, req, response)
                    && !await HandleMonthCardCommonAsync(session, req, response)
                    && !await HandleAdventureCommonAsync(session, req, response)) response.Result = ErrorCodes.Parameter;
            }
            if (!_deadlines.IsCurrent(session, lease)) return;
            if (session.Activities.ActivitiesReady) await SyncAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await session.SendGameAsync(Common, response);
        }
        finally
        {
            notices?.Dispose();
            session.ActivityGate.Release();
            localScope?.Dispose();
            socialScope?.Dispose();
            if (notices != null)
                try { await notices.FlushAsync(); }
                catch (Exception e) { _log.LogWarning(e, "Committed activity notice failed for {Role}", session.Role?.Id); }
        }
    }

    public async Task ClaimAsync(GameSession session, NewActivityGetRewardCommonReq req)
    {
        if (session.Role == null)
        {
            await session.SendGameAsync(GetReward, new NewActivityGetRewardCommonResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        var lease = _deadlines.Capture(session);
        LegacyOwnerScope? ownerScope = _loop.Owners != null
            ? await EnterLocalWithGateAsync(session, "activity/claim") : null;
        if (ownerScope == null) await session.ActivityGate.WaitAsync();
        try
        {
            if (!_deadlines.IsCurrent(session, lease)) return;
            if (session.Activities.ActivitiesReady && session.Activities.NewActivities != null)
            {
                await SettleEveExpiredAsync(session, lease);
                if (!_deadlines.IsCurrent(session, lease)) return;
                await RefreshCalendarAndSaveAsync(session);
            }
            int result = ActivityError(session, req.ActivityID);
            if (result == 0) result = await ClaimCoreAsync(session, req);
            if (!_deadlines.IsCurrent(session, lease)) return;
            if (session.Activities.ActivitiesReady) await SyncAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await session.SendGameAsync(GetReward, new NewActivityGetRewardCommonResp { Result = result });
        }
        finally { session.ActivityGate.Release(); ownerScope?.Dispose(); }
    }

    private async Task<int> ClaimCoreAsync(GameSession session, NewActivityGetRewardCommonReq req)
    {
        if (session.Activities.NewActivities!.ActivityInfo[req.ActivityID].ActivityParam.SevenDay is not { } seven)
            return await ClaimAdventureAsync(session, req);
        if (_data.Row("SevenDaySignUpConfig", req.Index) is not { } row) return ErrorCodes.ActivityReward;
        if (seven.RewardStatus.GetValueOrDefault(req.Index)) return ErrorCodes.RewardClaimed;
        if (seven.LoginDays < GameData.Int(row, "_iRewardDate")) return ErrorCodes.TaskRequirements;
        var next = session.Activities.NewActivities.Clone();
        next.ActivityInfo[req.ActivityID].ActivityParam.SevenDay.RewardStatus[req.Index] = true;
        next.ActivityTypeState[8].ActivityState[req.ActivityID].SelfData.SevenDay = next.ActivityInfo[req.ActivityID].ActivityParam.SevenDay.Clone();
        return await CommitAsync(session, next, [GameData.Int(row, "_iRewardID")],
            source: $"seven/{req.ActivityID}/{next.ActivityInfo[req.ActivityID].CycleStartTime}/{req.Index}");
    }

    private async Task<int> ClaimSimpleAsync(GameSession session, long activityId, long rewardId, bool preview)
    {
        int error = ActivityError(session, activityId, preview ? 4 : 3);
        if (error != 0) return error;
        if (_data.Row(preview ? "FunctionPreviewConfig" : "LevelAwardConfig", rewardId) is not { } row) return ErrorCodes.ActivityReward;
        var param = session.Activities.NewActivities!.ActivityInfo[activityId].ActivityParam;
        var claims = preview ? param.FunctionPreview : param.LevelAward;
        if (claims.AwardData.ContainsKey(rewardId)) return ErrorCodes.RewardClaimed;
        bool eligible = preview ? GameData.Int(row, "_iFunction") is var f and > 0
            ? _functions.IsOpen(session, f) : session.Progress!.Level >= GameData.Int(row, "_iLv")
            : session.Progress!.Level >= GameData.Int(row, "_iLevel");
        if (!eligible) return ErrorCodes.TaskRequirements;
        var next = session.Activities.NewActivities.Clone();
        var awardData = preview ? next.ActivityInfo[activityId].ActivityParam.FunctionPreview : next.ActivityInfo[activityId].ActivityParam.LevelAward;
        awardData.AwardData[rewardId] = Now; // existence is the client claim test; timestamp value is inferred
        return await CommitAsync(session, next, [GameData.Int(row, "_iAwardID")],
            source: $"simple/{activityId}/{next.ActivityInfo[activityId].CycleStartTime}/{rewardId}");
    }

    internal static ActivityRuntimeState CloneRuntime(GameSession session) =>
        JsonSerializer.Deserialize<ActivityRuntimeState>(JsonSerializer.SerializeToUtf8Bytes(session.Activities.ActivityRuntime))!;

    /// <summary>One source settlement, with source markers committed before any observable success or reward packet.</summary>
    internal async Task<int> CommitAsync(GameSession session, DBNewActivityInfo next, IEnumerable<long>? awardIds = null,
        IReadOnlyList<(int Entry, int Count)>? costs = null, IEnumerable<AwardGrant>? extra = null,
        ActivityRuntimeState? runtime = null,
        Action<Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder>? persist = null, bool stateLockHeld = false,
        Func<Task>? beforeSync = null, Action? committed = null, string? journeySource = null,
        string? source = null, EscrowEconomyIntent? economy = null)
    {
        if (economy != null && (journeySource != null || source != null))
            throw new InvalidOperationException("Activity settlement has more than one economic family.");
        var lease = _deadlines.Capture(session);
        long roleId = session.Role?.Id ?? 0;
        try
        {
            var resolvedIds = (awardIds ?? []).ToArray();
            var grants = resolvedIds.SelectMany(id => _awards.Resolve(session, id, Playbook(session)).Grants).Concat(extra ?? []).ToArray();
            if (economy != null)
            {
                EconomyAsset Asset(int entry) => _data.Row("ItemConfig", entry) is { } row
                    ? new(GameData.Int(row, "_iType") == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry)
                    : throw new AwardResolutionException($"Activity economy entry {entry} is not configured");
                foreach (var group in (costs ?? []).Where(c => c.Count > 0).GroupBy(c => c.Entry))
                    economy.Sink(Asset(group.Key), group.Sum(c => (long)c.Count), EconomyReason.ItemUse);
                foreach (var group in grants.Where(g => g.Entry is not (100 or 101)).GroupBy(g => g.Entry))
                    economy.Source(Asset(group.Key), group.Sum(g => (long)g.Count), EconomyReason.ActivityReward);
                economy.Evidence(new
                {
                    AwardIds = resolvedIds, Costs = costs ?? [], Grants = grants.Select(g =>
                    new { g.Entry, g.Count, g.BindType, g.ExpireTimeStamp }).ToArray(),
                    Exp = grants.Where(g => g.Entry is 100 or 101).Select(g => new { g.Entry, g.Count }).ToArray()
                });
            }
            byte[] infoBytes = next.ToByteArray(), runtimeBytes = JsonSerializer.SerializeToUtf8Bytes(runtime ?? session.Activities.ActivityRuntime);
            var journeyEconomy = journeySource == null ? null : new CharacterService.OwnedAwardSource(
                "cd13e/journey-victory", journeySource,
                new { Source = journeySource, State = Convert.ToBase64String(infoBytes), Grants = grants });
            bool accepted = await _character.ApplyAwardAsync(session, new AwardPlan(0, 0, grants), costs: costs, persist: tx =>
            {
                if (!_deadlines.IsCurrent(session, lease) || session.Role?.Id != roleId)
                    throw new OperationCanceledException("Activity attachment changed before award commit.");
                tx.RoleBlob(roleId, "new_activities", infoBytes);
                tx.RoleBlob(roleId, "activity_runtime", runtimeBytes);
                persist?.Invoke(tx);
                if (economy != null) tx.WithEscrowEconomy(economy);
                else if (source != null && ((costs?.Any(cost => cost.Entry is not (100 or 101) && cost.Count > 0) ?? false)
                    || grants.Any(grant => grant.Entry is not (100 or 101) && grant.Count > 0)))
                    tx.WithEventEconomy(EventEconomy.ForRole(_data, session, "newactivity",
                        $"newactivity/{roleId}/{source}",
                        (costs ?? []).Select(cost => (cost.Entry, (long)cost.Count)), grants,
                        new { Source = source, Activity = Convert.ToBase64String(infoBytes), Runtime = Convert.ToBase64String(runtimeBytes) }));
            }, stateLockHeld: stateLockHeld, committed: () =>
            {
                session.Activities.NewActivities = next;
                if (runtime != null) session.Activities.ActivityRuntime = runtime;
                committed?.Invoke();
            }, ownedAward: journeyEconomy);
            if (!_deadlines.IsCurrent(session, lease) || session.Role?.Id != roleId)
                throw new OperationCanceledException("Activity attachment changed during award delivery.");
            if (!accepted) return _character.ShortageOf(session, [.. (costs ?? []).Select(c => (c.Entry, (long)c.Count))]);
            if (beforeSync != null) await beforeSync();
            if (!_deadlines.IsCurrent(session, lease))
                throw new OperationCanceledException("Activity attachment changed before projection delivery.");
            await SyncAsync(session);
            if (!_deadlines.IsCurrent(session, lease))
                throw new OperationCanceledException("Activity attachment changed during projection delivery.");
            return 0;
        }
        catch (AwardResolutionException e)
        {
            _log.LogWarning("Activity award rejected for {Role}: {Reason}", session.Role?.Id, e.Message);
            return ErrorCodes.RewardMissing;
        }
    }

    private async Task SaveCalendarAsync(GameSession session, DBNewActivityInfo next, byte[]? before,
        bool initializeRuntime = false)
    {
        bool chapterChanged = false;
        if (next.ActivityInfo.GetValueOrDefault(ExplorationActivity)?.ActivityParam?.WorldExploration is { } world)
            foreach (var phase in world.PhaseInfos.Values)
                chapterChanged |= UpdateChapterProgress(session, phase);
        byte[] info = next.ToByteArray();
        bool changed = before == null || !info.AsSpan().SequenceEqual(before);
        if (!changed && !initializeRuntime) return;
        var tx = _roles.NewTransaction();
        if (changed) tx.RoleBlob(session.Role!.Id, "new_activities", info);
        if (initializeRuntime) tx.RoleBlob(session.Role!.Id, "activity_runtime",
            JsonSerializer.SerializeToUtf8Bytes(session.Activities.ActivityRuntime));
        await _roles.CommitAsync(tx, "activity/calendar", _ => session.Activities.NewActivities = next);
        if (chapterChanged && session.Activities.ActivitiesReady) _tasks.OnStateChanged(session);
    }

    private async Task RefreshCalendarAndSaveAsync(GameSession session)
    {
        byte[] before = session.Activities.NewActivities!.ToByteArray();
        var next = session.Activities.NewActivities!.Clone();
        RefreshCalendar(session, next);
        await SaveCalendarAsync(session, next, before);
    }

    internal async Task SyncAsync(GameSession session)
    {
        if (session.Activities.NewActivities == null) return;
        var lease = _deadlines.Capture(session);
        if (!_deadlines.IsCurrent(session, lease)) return;
        var state = session.Activities.NewActivities.Clone();
        if (state.ActivityInfo.TryGetValue(118, out var info)) info.ActivityParam.PronteraDefense = _defense.BuildPlayerState(session);
        foreach (var id in state.ActivityInfo.Keys.ToArray())
            if (_data.Row("NewActivityConfig", id) is not { } row || !Applies(session, row)) state.ActivityInfo.Remove(id);
        if (TerritoryWarState is { } territory)
            foreach (var (id, activity) in state.ActivityInfo)
                if (_data.Row("NewActivityConfig", id) is { } row && GameData.Int(row, "_iActivityType") == 22)
                {
                    activity.ActivityParam ??= new();
                    activity.ActivityParam.TerritoryWar = territory(session);
                }
        // The initial nested maps are handled in the projection partial; persistent state never contains wire markers.
        await SyncProjectionAsync(session, state, lease);
        if (!_deadlines.IsCurrent(session, lease)) return;
        var exploration = state.ActivityInfo.Values.Select(i => i.ActivityParam?.WorldExploration).FirstOrDefault(i => i != null);
        if (exploration != null) await CharacterService.SyncAsync(session, WorldExplorationSync, exploration.Clone());
        if (!_deadlines.IsCurrent(session, lease)) return;
        var month = state.ActivityInfo.Values.Select(i => i.ActivityParam?.MonthCard).FirstOrDefault(i => i != null);
        if (month != null) await CharacterService.SyncAsync(session, MonthCardSync, month.Clone());
        if (!_deadlines.IsCurrent(session, lease)) return;
        await SyncTreasureAsync(session, lease);
        if (!_deadlines.IsCurrent(session, lease)) return;
        if (session.Activities.ActivitiesReady)
        {
            await SyncConchesAsync(session);
            if (exploration != null) await _life.SyncRestrictionsAsync(session);
            ScheduleDeadline(session);
        }
        foreach (var changed in ProjectionChanged.Handlers) changed(session);
    }

    public Task ReconcileAsync(GameSession session) => ReconcileAsync(session, null);

    private async Task ReconcileAsync(GameSession session, DeadlineLease? scheduled)
    {
        if (!_loop.IsOnLoop) { await _loop.InvokeAsync(() => ReconcileAsync(session, scheduled)); return; }
        if (!session.Activities.ActivitiesReady || session.Role == null || session.Activities.NewActivities == null) return;
        var lease = scheduled ?? _deadlines.Capture(session);
        var ownerScope = _loop.Owners != null
            ? await EnterLocalWithGateAsync(session, "activity reconciliation") : null;
        if (ownerScope == null) await session.ActivityGate.WaitAsync();
        try
        {
            if (!session.Activities.ActivitiesReady || session.Role == null || session.Activities.NewActivities == null
                || !_deadlines.IsCurrent(session, lease)) return;
            await SyncServerDayAsync(session);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await SettleEveExpiredAsync(session, lease);
            if (!_deadlines.IsCurrent(session, lease)) return;
            await RefreshCalendarAndSaveAsync(session);
            // A held async calendar commit can cross the realm-open day boundary. Refresh the
            // day marker after that commit and before publishing the resulting projection.
            await SyncServerDayAsync(session);
            // A successful pass commit replaces this service schedule; subsequent phases belong to
            // the same attachment, not to the superseded schedule generation.
            var attachment = lease with { ScheduleGeneration = 0, Key = null };
            await ReconcilePassAsync(session);
            if (!_deadlines.IsCurrent(session, attachment)) return;
            await ReconcileMonthCardAsync(session);
            if (!_deadlines.IsCurrent(session, attachment)) return;
            await SyncAsync(session);
        }
        finally { session.ActivityGate.Release(); ownerScope?.Dispose(); }
    }

    private async Task SyncServerDayAsync(GameSession session)
    {
        // LC_DAL_ServerInfo.PackServerBasicInfo uses floor((logic-open)/86400)+1.
        long day = (long)Math.Floor((Now - _activityClock.ServerOpen) / 86400d) + 1;
        if (session.Activities.ActivityServerDay == day) return;
        session.Activities.ActivityServerDay = day;
        await session.SendGameAsync(30130, new ServerBasicInfo
        { ServerOpenTime = (ulong)_activityClock.ServerOpen, ServerLogicTime = (ulong)Now });
        await _functions.SyncAsync(session);
    }

    internal void QueueRefresh(GameSession session)
    {
        if (!session.Activities.ActivitiesReady) return;
        if (!_loop.IsOnLoop)
        {
            var role = session.Role;
            int serial = session.AccountEntitySerial;
            _loop.PostRuntime(() =>
            {
                if (ReferenceEquals(session.Role, role) && session.AccountEntitySerial == serial) QueueRefresh(session);
                return Task.CompletedTask;
            });
            return;
        }
        var lease = _deadlines.Capture(session);
        if (lease.AttachmentGeneration == 0) return;
        var queuedKey = (session, lease.AttachmentGeneration);
        lock (_queued) if (!_queued.Add(queuedKey)) return;
        _loop.PostRuntime(async () =>
        {
            try { if (_deadlines.IsCurrent(session, lease)) await ReconcileAsync(session); }
            finally { lock (_queued) _queued.Remove(queuedKey); }
        });
    }

    /// <summary>Territory and other projection owners call this after their committed state changes.</summary>
    public void InvalidateProjection(GameSession session) => QueueRefresh(session);
}

/// <summary>Server-owned activity watermarks complementing the client DBNewActivityInfo protocol.</summary>
public sealed partial class ActivityRuntimeState;
