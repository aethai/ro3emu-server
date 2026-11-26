using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ordinary dungeons: 30400 selection, 42033/30406 preformed-team ready check,
/// 70010/30005 private scene, 30401/30404 state, authoritative kill settlement 30403/82584, 5062 and exit.
/// Encounter scripts absent from the export are explicitly bounded in DungeonCatalog; no client event grants wins
/// directly. A reconstructed scripted scene (DungeonService.Scripted) advances only on its configured client events
/// (an interaction on its own shown actor, a walk into its region, the end of the story it started).
/// </summary>
public sealed partial class DungeonService(GameData data, DungeonCatalog catalog, RoleStore roles,
    DungeonObligationStore obligations, CharacterService character,
    FunctionService functions, SeasonService seasons, InstanceService instances, TeamService teams,
    SessionRegistry sessions, RewardService rewards, AuctionService auctions, GuildStore guilds, CombatService combat,
    GameLoop loop, ILogger<DungeonService> log, Microsoft.Extensions.Options.IOptions<ServerOptions> options,
    PraiseService praise, TestingSwitches testing) : Messaging.INpcInteractionHandler
{
    int Messaging.INpcInteractionHandler.Order => 200;
    Task<bool> Messaging.INpcInteractionHandler.TryInteractAsync(GameSession session, NpcInteractionReq request) =>
        TryInteractAsync(session, request);
    public const uint Sync = 5062, Enter = 30400, BaseInfo = 30401, Leave = 30402, Result = 30403, Event = 30404,
        Confirm = 30406, ClientEvent = 30106, CreateResult = 30107, EnterSceneResult = 30108,
        Reset = 30124, ReadyQueue = 42033, TeamInfo = 30441, ConfirmReminder = 42047, ConfirmReminderNotify = 42046,
        AwardNotify = 82584, ClaimFirstPass = 82587, SetJobPosition = 82590, SetGuide = 82592;
    public const uint ReviveInfo = 30112;
    /// <summary>84012 PlotActingTriggerReq: a story's end action 150 PlotActingTrigger, sent beside 84011.</summary>
    public const uint PlotSceneEvent = 84012;
    /// <summary>DungeonVariableConfig 301 CCountdownEffectTimestamp (Unix ms the count reaches zero) and 302
    /// CCountdownEffectParamList {duration ms, UIWords id}: LC_DunGeon_GVE_FuncMappingClientBase maps both to
    /// LC_DunGeon_Actions.UpdateCountdownEffectParam, which opens UI_Sys_GVE_BattleBeginsPanel.</summary>
    internal const int CountdownEffectTimestampVar = 301, CountdownEffectParamsVar = 302;

    /// <summary>
    /// 30404 DungeonEventInvokeClientFunc: LC_DunGeon_Base.CELL_DUNGEON_EVENT_NOTIFY (LC_DunGeon_Base.lua:1265-1290)
    /// calls the named function through LC_Dungeon_ScriptCaller.CallClientFuncWithSelf (the level, its client logic
    /// script, then LC_DunGeon_Actions) with one argument per EventParams, the first value of each
    /// (LC_DunGeon_Base.lua:342-430). The client does not read stage and level for this event; 1/1 is (inferred).
    /// </summary>
    internal static DungeonEventNotify InvokeClientFunc(long scene, string function, params EventParams[] args) => new()
    {
        DungeonEntry = scene, EventType = DungeonEventType.DungeonEventInvokeClientFunc, StageNum = 1, LevelNum = 1,
        CliFuncname = function, Params = { args },
    };

    /// <summary>Committed clears only; consumers receive the scene entry and whether an actual personal award was paid.</summary>
    public AsyncHook<GameSession, int, bool> Completed { get; } = new("DungeonService.Completed");
    /// <summary>After <see cref="Completed"/> for a TenGameplay run: the boss (TenGameplayBossConfig._iBoss) and the
    /// run's settled participants (quest target 106602).</summary>
    public AsyncHook<GameSession, long, IReadOnlyCollection<GameSession>> MultiBossCleared { get; } = new("DungeonService.MultiBossCleared");
    /// <summary>A loaded member's dungeon task tracker (TaskService.OnDungeonRun): scene, instance and the scripted steps
    /// the run has passed. Listeners post their work.</summary>
    public SyncHook<GameSession, long, long, int> RunTracked { get; } = new("DungeonService.RunTracked");
    /// <summary>Guild Challenge owns admission and rewards; this engine retains scenes, combat and cleanup.</summary>
    internal Func<GameSession, EnterDungeonReq, Task<bool>>? GuildChallengeEnter { get; set; }
    /// <summary>Another private-room owner whose 30402 exit revives a dead member on return (the guild home).</summary>
    internal Func<long, bool>? RecoverDeadOnLeave { get; set; }
    /// <summary>A preformed-team ready check is waiting on this member.</summary>
    internal bool Preparing(GameSession s) => _pending.ContainsKey(s) || Scripted._rolling.Contains(s);

    private readonly GameData _data = data;
    private readonly DungeonCatalog _catalog = catalog;
    private readonly RoleStore _roles = roles;
    private readonly DungeonObligationStore _obligations = obligations;
    private readonly CharacterService _character = character;
    private readonly FunctionService _functions = functions;
    private readonly SeasonService _seasons = seasons;
    private readonly InstanceService _instances = instances;
    private readonly TeamService _teams = teams;
    private readonly SessionRegistry _sessions = sessions;
    private readonly RewardService _rewards = rewards;
    private readonly AuctionService _auctions = auctions;
    private readonly GuildStore _guilds = guilds;
    private readonly CombatService _combat = combat;
    private readonly GameLoop _loop = loop;
    private readonly ILogger<DungeonService> _log = log;
    private readonly ServerOptions _options = options.Value;
    private readonly PraiseService _praise = praise;
    private readonly TestingSwitches _testing = testing;
    private readonly Dictionary<GameSession, Preparation> _pending = [];
    private TimeProvider _clock => _loop.Time;
    private Ownership.OwnerRegistry _owners => _loop.Owners!;

    internal GameData GameData => _data;
    internal DungeonCatalog Catalog => _catalog;
    internal Ownership.OwnerRegistry Owners => _owners;
    internal CharacterService Character => _character;
    internal FunctionService Functions => _functions;
    internal SeasonService Seasons => _seasons;
    internal InstanceService Instances => _instances;
    internal TeamService Teams => _teams;
    internal SessionRegistry Sessions => _sessions;
    internal GuildStore Guilds => _guilds;
    internal GameLoop Loop => _loop;
    internal TimeProvider Clock => _clock;
    internal ILogger<DungeonService> Log => _log;
    internal ServerOptions Options => _options;

    internal RaidTrialsService Raids { get; private set; } = null!;
    internal ScriptedDungeonService Scripted { get; private set; } = null!;

    internal void BindSubsystems(RaidTrialsService raids, ScriptedDungeonService scripted)
    {
        Raids = raids;
        Scripted = scripted;
        raids.WireCombat();
        scripted.WireScene();
        _combat.Killed.Subscribe(KilledAsync);
        _combat.BeforeKilled.Subscribe(FreezeTerminalKillAsync, order: CombatService.BeforeKilledOrderDungeon);
        _combat.PlayerDied.Subscribe(PlayerDiedAsync);
        _combat.ReviveInfoOwners.Subscribe(s => Active(s) != null); // PlayerDiedAsync publishes the run's own 30112
        _combat.Dealt.Subscribe((s, m, n) => { if (Active(s) is { } a && m.InstanceId == a.Room.Id) a.Stats[s.Role!.Id].TotalDamage += n; });
        _combat.Taken.Subscribe((m, s, n, dead) => { if (Active(s) is { } a && m.InstanceId == a.Room.Id) a.Stats[s.Role!.Id].TotalBeDamage += n; });
        _combat.Healed.Subscribe((s, n) => { if (Active(s) is { } a) a.Stats[s.Role!.Id].TotalCure += n; });
        _character.ProgressHandlers.Subscribe(SyncLockedAsync); // CharacterService invokes these while holding StateLock.
    }

    // ---- RaidTrialsService & ScriptedDungeonService facades ----

    public const uint HallOfFame = RaidTrialsService.HallOfFame, MarkBlessings = RaidTrialsService.MarkBlessings,
        ClaimHonor = RaidTrialsService.ClaimHonor, ModuleButton = RaidTrialsService.ModuleButton;
    internal const string ServerInfoKey = RaidTrialsService.ServerInfoKey;
    internal static readonly long[] StartBattleScenes = RaidTrialsService.StartBattleScenes;
    internal const int FirstAwardNone = RaidTrialsService.FirstAwardNone,
        FirstAwardComplete = RaidTrialsService.FirstAwardComplete,
        FirstAwardFinish = RaidTrialsService.FirstAwardFinish;
    public const uint Statistics = RaidTrialsService.Statistics, StatisticsHistory = RaidTrialsService.StatisticsHistory,
        StatisticsModule = RaidTrialsService.StatisticsModule, StatisticsCurrent = RaidTrialsService.StatisticsCurrent,
        PlayerStatistics = RaidTrialsService.PlayerStatistics, TaskScore = RaidTrialsService.TaskScore,
        QueryResult = RaidTrialsService.QueryResult, ResultNotify = RaidTrialsService.ResultNotify;
    public const uint RandomDungeon = ScriptedDungeonService.RandomDungeon;
    public const int RegionEnterDungeonAction = ScriptedDungeonService.RegionEnterDungeonAction;
    public const int StoryVar = ScriptedDungeonService.StoryVar;
    internal const int GuideWayVar = ScriptedDungeonService.GuideWayVar;
    internal const int PlaneExitSeconds = ScriptedDungeonService.PlaneExitSeconds;
    internal const long SceneActorUidBase = ScriptedDungeonService.SceneActorUidBase;
    internal const long SlideStateData = ScriptedDungeonService.SlideStateData,
        SlideSpeedData = ScriptedDungeonService.SlideSpeedData,
        SlideCameraData = ScriptedDungeonService.SlideCameraData;

    /// <summary>The exact act and activity generation earned by the frozen dungeon source. A later
    /// activity cycle must never consume this older victory or draw its result again.</summary>
    public sealed record JourneyVictory(string Source, long SceneEntry, long ActId, long NodeId,
        long ActivityStart, long ActivityEnd, long PlayBook);

    internal DBMultiDungeonServerInfo ServerInfo => Raids.ServerInfo;
    internal Func<GameSession, DungeonStatisticsInfoWithDungeon?>? CurrentStatisticsOwner
    { get => Raids.CurrentStatisticsOwner; set => Raids.CurrentStatisticsOwner = value; }
    internal Func<GameSession, JourneyVictory, Action<Accounts.Persistence.RoleItemPlanBuilder>, Action, Task<bool>>? JourneyWon
    { get => Scripted.JourneyWon; set => Scripted.JourneyWon = value; }
    internal Func<GameSession, bool>? CanStageJourneyVictory
    { get => Scripted.CanStageJourneyVictory; set => Scripted.CanStageJourneyVictory = value; }
    internal Func<GameSession, CustomData, Task>? SetCustomData
    { get => Scripted.SetCustomData; set => Scripted.SetCustomData = value; }

    public static long PositionFor(GameData data, GameSession s) => RaidTrialsService.PositionFor(data, s);
    internal static long[] PositionsFor(GameData data, long job) => RaidTrialsService.PositionsFor(data, job);
    internal static GameSession? CompanionCreditAmong(IReadOnlyList<GameSession> loaded) => RaidTrialsService.CompanionCreditAmong(loaded);
    public Task ClaimFirstPassAsync(GameSession s, ClaimFirstPassAwardReq req) => Raids.ClaimFirstPassAsync(s, req);
    public Task HallOfFameAsync(GameSession s, QueryHallOfFameInfoReq req) => Raids.HallOfFameAsync(s, req);
    internal Task StoreFirstClearAsync(DBMultiDungeonHallOfFame record, bool replace = false) => Raids.StoreFirstClearAsync(record, replace);
    internal (DBMultiDungeonSacredBlessings Info, JsonElement? Row)? Blessing(JsonElement boss) => Raids.Blessing(boss);
    public Task MarkBlessingsAsync(GameSession s, MultiDungeonMarkSacredBlessingsReq req) => Raids.MarkBlessingsAsync(s, req);
    public Task ClaimHonorAsync(GameSession s, MultiDungeonClaimHonorAwardReq req) => Raids.ClaimHonorAsync(s, req);
    public Task ModuleButtonAsync(GameSession s, DungeonModuleButtonReq req) => Raids.ModuleButtonAsync(s, req);
    public Task StatisticsAsync(GameSession s) => Raids.StatisticsAsync(s);
    public Task StatisticsCurrentAsync(GameSession s) => Raids.StatisticsCurrentAsync(s);
    public Task StatisticsHistoryAsync(GameSession s, DungeonStatisticsInfoWithDungeonReq req) => Raids.StatisticsHistoryAsync(s, req);
    public Task StatisticsModuleAsync(GameSession s) => Raids.StatisticsModuleAsync(s);
    public Task QueryResultAsync(GameSession s, QueryDungeonResultReq req) => Raids.QueryResultAsync(s, req);
    public Task StoreExternalResultAsync(GameSession s, DungeonResultNotify notice) => Raids.StoreExternalResultAsync(s, notice);
    public static DungeonPlayerStatisticsNtf PlayerCounters(long roleId, long kills, long assists) =>
        RaidTrialsService.PlayerCounters(roleId, kills, assists);
    internal static bool InTrialArea(TrialUnit o, float radius, float x, float z) => RaidTrialsService.InTrialArea(o, radius, x, z);
    internal static (List<GameSession> Passed, List<GameSession> Failing, bool PartyFailed) Judge(ReconstructedDungeons.RaidTrial trial,
        TrialRun run, MonsterUnit boss, IReadOnlyList<GameSession> fighters) => RaidTrialsService.Judge(trial, run, boss, fighters);
    internal static bool Sheltered(float bossX, float bossZ, float coverX, float coverZ, float x, float z, ReconstructedDungeons.Shelter shelter) =>
        RaidTrialsService.Sheltered(bossX, bossZ, coverX, coverZ, x, z, shelter);
    internal static bool Sheltered(float bossX, float bossZ, float coverX, float coverZ, float x, float z, float width, float depth) =>
        RaidTrialsService.Sheltered(bossX, bossZ, coverX, coverZ, x, z, width, depth);

    internal Task<Attempt?> EnterDraculaAsync(GameSession s, DungeonCatalog.Plan plan, IDungeonMode mode, Attempt? existing) =>
        Scripted.EnterDraculaAsync(s, plan, mode, existing);
    internal Task StopDraculaAsync(Attempt a) => Scripted.StopDraculaAsync(a);
    internal static IReadOnlyList<GameSession> LoadedMembers(Attempt a) => ScriptedDungeonService.LoadedMembers(a);
    internal Task SendToRoomAsync(Attempt a, uint id, IMessage msg) => Scripted.SendToRoomAsync(a, id, msg);
    internal Task<Attempt?> EnterGuildChallengeAsync(GameSession s, DungeonCatalog.Plan plan, IDungeonMode mode, Attempt? existing) =>
        Scripted.EnterGuildChallengeAsync(s, plan, mode, existing);
    internal Task StartGuildChallengeAsync(Attempt a) => Scripted.StartGuildChallengeAsync(a);
    internal Task StopGuildChallengeAsync(Attempt a) => Scripted.StopGuildChallengeAsync(a);
    internal List<DungeonCatalog.Plan> RandomPool(DungeonCatalog.Plan origin, IReadOnlyCollection<GameSession> members) =>
        Scripted.RandomPool(origin, members);
    public Task DeliverJourneyPendingAsync(GameSession s) => Scripted.DeliverJourneyPendingAsync(s);
    public Task<int> RestartJourneyAsync(GameSession session) => Scripted.RestartJourneyAsync(session);
    public Task<bool> EnterFromRegionAsync(GameSession s, MapRegion region) => Scripted.EnterFromRegionAsync(s, region);
    public Task<bool> TryInteractAsync(GameSession s, NpcInteractionReq req) => Scripted.TryInteractAsync(s, req);
    public Task PlotEndedAsync(GameSession s, long plotId) => Scripted.PlotEndedAsync(s, plotId);
    public Task PlayerMovedAsync(GameSession s) => Scripted.PlayerMovedAsync(s);
    internal static float RideLength(IReadOnlyList<(float X, float Y, float Z)> path) => ScriptedDungeonService.RideLength(path);
    internal static (float X, float Y, float Z, float Face) RideAt(IReadOnlyList<(float X, float Y, float Z)> path, float metres) =>
        ScriptedDungeonService.RideAt(path, metres);
    internal static (int Leg, (float X, float Y, float Z) From, (float X, float Y, float Z) To) RideLeg(
        IReadOnlyList<(float X, float Y, float Z)> path, float metres) => ScriptedDungeonService.RideLeg(path, metres);
    internal static IEnumerable<(float X, float Y, float Z, float Face)> RideCorners(IReadOnlyList<(float X, float Y, float Z)> path,
        float start, float end) => ScriptedDungeonService.RideCorners(path, start, end);
    internal static bool Sliding(GameSession s) => ScriptedDungeonService.Sliding(s);

    private void StartRaidTrials(Attempt a) => Raids.StartRaidTrials(a);
    private Task RefreshBondsAsync(Attempt attempt) => Raids.RefreshBondsAsync(attempt);
    private Task ClearBondsAsync(GameSession s, Attempt attempt) => Raids.ClearBondsAsync(s, attempt);
    private static int FirstPassAwardState(State state, string key, JsonElement boss, Record? record) =>
        RaidTrialsService.FirstPassAwardState(state, key, boss, record);
    private void RecordFirstClear(Attempt a, JsonElement boss, GameSession[] party) => Raids.RecordFirstClear(a, boss, party);
    private static int BlessingRevives(Attempt a) => RaidTrialsService.BlessingRevives(a);
    private static DBMultiDungeonHonor? HonorView(State state, string key, JsonElement boss) => RaidTrialsService.HonorView(state, key, boss);
    private void EarnHonors(State next, Attempt a, JsonElement boss, long durationMs) => Raids.EarnHonors(next, a, boss, durationMs);
    private static DBMultiDungeonHonor? EarnedHonors(Accounts.DungeonRewardObligation row) => RaidTrialsService.EarnedHonors(row);
    private static DungeonVar? FunctionButtons(Attempt a) => RaidTrialsService.FunctionButtons(a);
    private Task EndPreparationAsync(Attempt a) => Raids.EndPreparationAsync(a);
    private void StoreHistory(State state, Attempt a) => Raids.StoreHistory(state, a);
    private Task FlyAsync(Attempt a, MonsterUnit unit, IReadOnlyList<(float X, float Y, float Z)> route) => Raids.FlyAsync(a, unit, route);
    private TimeSpan Paced(int ms) => Raids.Paced(ms);
    private long ContinuationOf(long scene) => Scripted.ContinuationOf(scene);
    private void ScheduleContinuation(GameSession s, Attempt a, long next) => Scripted.ScheduleContinuation(s, a, next);
    private DungeonVar? GuideWays(Attempt a) => Scripted.GuideWays(a);
    private Task HideGuideWaysAsync(GameSession s, Attempt a) => Scripted.HideGuideWaysAsync(s, a);
    private static bool IsRandom(DungeonCatalog.Plan plan) => ScriptedDungeonService.IsRandom(plan);
    private Task RollAsync(DungeonCatalog.Plan origin, GameSession[] members, long leader, long team, bool matched) =>
        Scripted.RollAsync(origin, members, leader, team, matched);
    private JourneyVictory? CaptureJourneyVictory(GameSession s, long sceneEntry, string source) =>
        Scripted.CaptureJourneyVictory(s, sceneEntry, source);
    private bool JourneyCanEnter(GameSession session, long sceneEntry) => Scripted.JourneyCanEnter(session, sceneEntry);
    private static bool LeavesAfterClear(Attempt a) => ScriptedDungeonService.LeavesAfterClear(a);
    private void ScheduleLeaveAfterClear(GameSession s, Attempt a) => Scripted.ScheduleLeaveAfterClear(s, a);
    private static ReconstructedDungeons.ScriptedStep? Step(Attempt a) => ScriptedDungeonService.Step(a);
    private Task BeginStepAsync(Attempt a, ReconstructedDungeons.ScriptedStep step) => Scripted.BeginStepAsync(a, step);

    public sealed class Attempt
    {
        /// <summary>Who sits on a scene actor now (a ride, DungeonService.Ride), with the seat's token.</summary>
        internal Dictionary<GameSession, long> Riders { get; } = [];

        /// <summary>The ride its seated rider drives now (DungeonService.Ride), null when none.</summary>
        internal Drive? Driving { get; set; }

        public required InstanceService.Room Room { get; init; }
        public required DungeonCatalog.Plan Plan { get; init; }
        public required long Leader { get; init; }
        public required long TeamId { get; init; }
        public required int PlayBook { get; init; }
        public required int SeasonId { get; init; }
        public bool Matched { get; init; }
        public HashSet<GameSession> Loaded { get; } = [];
        /// <summary>30441 roster slots by role (index = 0-based position), fixed on the first roster.</summary>
        public List<long> Slots { get; } = [];
        public HashSet<long> Killed { get; } = [];
        /// <summary>The completing events a step with "all" has seen so far.</summary>
        public HashSet<(ReconstructedDungeons.TriggerKind Kind, long Id)> StepSeen { get; } = [];
        /// <summary>A scripted step's strikes taken so far, by monster uid.</summary>
        /// <summary>A step's own timers' generation: each step (and each new hide-and-seek game) starts a new one, so a
        /// stale timer does nothing.</summary>
        public int StepGeneration { get; set; }
        /// <summary>The running step's <see cref="ReconstructedDungeons.ScriptedStep.HoldAfterMs"/> was started / is over.</summary>
        public bool StepHeld { get; set; }
        public bool StepHoldOver { get; set; }
        /// <summary>A hide-and-seek step: the round being played (1-based) and the hiding places already searched in
        /// this game.</summary>
        public int SeekRound { get; set; }
        public HashSet<long> Searched { get; } = [];
        public HashSet<long> Precounted { get; } = [];
        public bool ObligationsFrozen { get; set; }
        public bool ObligationsActivated { get; set; }
        public bool FrozenWin { get; set; }
        public MonsterUnit? TerminalMonster { get; set; }
        public long TerminalKillerRole { get; set; }
        public Ownership.AttachmentLease? TerminalKillerAttachment { get; set; }
        public string? TerminalKillSource { get; set; }
        public RewardSettlement? ActivationSettlement { get; set; }
        internal SemaphoreSlim FreezeGate { get; } = new(1, 1);
        public Dictionary<long, RoleStatisticsInfo> Stats { get; } = [];
        public Dictionary<GameSession, Dictionary<int, long>> StartingItems { get; } = [];
        public HashSet<GameSession> Paid { get; } = [];
        public Dictionary<long, int> RevivesUsed { get; } = [];
        public Dictionary<long, long> AutoReviveAt { get; } = [];
        public HashSet<GameSession> Reviving { get; } = [];
        public bool Started { get; set; }
        public bool Settled { get; set; }
        public int Wave { get; set; }
        public int Spawned { get; set; }
        /// <summary>Spawned before the current wave (its own kill count is Killed.Count minus this).</summary>
        public int WaveBase { get; set; }
        public long StartMs { get; set; }
        public TimeSpan StartedAt { get; set; }
        public bool Advancing { get; set; }
        public bool Entering { get; set; } = true;
        public bool Spawning { get; set; }
        /// <summary>The current scripted step got one of its completing client events (DungeonService.Scripted).</summary>
        public bool StepDone { get; set; }
        /// <summary>The wave whose scripted step was completed, so its counter reads full and the next one's does not.</summary>
        public int GoalFullWave { get; set; } = -1;
        /// <summary>A Realm of the Gods raid waits in its preparation phase (DungeonService.RealmOfGods).</summary>
        public bool Preparing { get; set; }
        /// <summary>The boss's Sacred Blessing level (TenGameplayBuffConfig row) active for this run, fixed at its start.</summary>
        public JsonElement? Blessing { get; set; }
        /// <summary>The scripted scene actors shown now (also tracked by the room for cleanup).</summary>
        internal List<SceneActorUnit> SceneActors { get; } = [];
        /// <summary>Scene actors an earlier step took away: a late auto interaction on one is answered without an error.</summary>
        internal HashSet<long> RetiredActors { get; } = [];
        /// <summary>The scripted step's allied companions standing now (DungeonService.Companions; room props too).</summary>
        internal List<CompanionUnit> Companions { get; } = [];
        /// <summary>The companions (NPC, point) that fell in this run: they do not come back.</summary>
        internal HashSet<(int NpcId, long Point)> FallenCompanions { get; } = [];
        /// <summary>A raid boss's scripted trials in this run (DungeonService.RaidTrials); null: none.</summary>
        internal RaidRun? Raid { get; set; }
        /// <summary>Feature-owned special dungeon mode (e.g. Guild Challenge, Dracula's Lair); null for ordinary/raid runs.</summary>
        internal IDungeonMode? Mode { get; set; }
    }

    private sealed class Preparation
    {
        public required DungeonCatalog.Plan Plan;
        public required GameSession Leader;
        public required GameSession[] Members;
        public required long TeamId;
        public readonly HashSet<GameSession> Ready = [];
        public bool Closed;
    }

    internal static Attempt? Active(GameSession s) => s.DungeonAttempt is { Started: true, Settled: false } a
        && !a.Room.Closed && s.InstanceId == a.Room.Id && !s.SceneLoading ? a : null;
    internal static GameSession[] Present(Attempt a) =>
        [.. a.Room.ActiveMembers.Select(m => m.Session).Where(s => ReferenceEquals(s.DungeonAttempt, a) && s.InstanceId == a.Room.Id && s.Role != null)];
    internal static Dictionary<int, long> Inventory(GameSession s)
    {
        var counts = s.Items.GroupBy(i => i.Entry).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Stack));
        foreach (var (id, n) in s.Progress!.Currency) counts[id] = n;
        return counts;
    }

    internal int Admission(GameSession s, DungeonCatalog.Plan plan, bool continuation = false)
    {
        if (s.Role == null || s.Instances.Dungeons == null || s.Progress == null) return ErrorCodes.Parameter;
        if (s.SeasonState is not { } state || state.SeasonId != _seasons.SeasonId || state.PlayBookId != _seasons.PlayBook)
            return ErrorCodes.FeatureUnavailable;
        if (s.SceneLoading) return ErrorCodes.SwitchingScenes;
        if (s.Dead) return ErrorCodes.FeatureUnavailable;
        if (s.InstanceId != 0 && !(continuation && s.DungeonAttempt is { Settled: true })) return ErrorCodes.DungeonBusy;
        if (!_functions.ConditionMet(s, GameData.Int(plan.Config, "_iDungoenLockCondition"))
            || !_functions.ConditionMet(s, GameData.Int(plan.Config, "_iDungeonConditionConfig"))) return ErrorCodes.FeatureUnavailable;
        if (GameData.Int(plan.Config, "_iGamePlayType") == 24 && !JourneyCanEnter(s, plan.SceneEntry))
            return ErrorCodes.TaskRequirements;
        if (plan.Multi is { } multi)
        {
            int function = GameData.Int(multi, "_iDungeonType") == 10 ? 61 : 62;
            if (!TowerService.FitsPlayBook(multi, _seasons.PlayBook) || !_functions.IsOpen(s, function)
                || !Meets(s, multi) || plan.Boss is not { } boss || !Meets(s, boss))
                return ErrorCodes.FeatureUnavailable;
        }
        return 0;
    }

    /// <param name="alone">Enter only this role even in a party (a story region stepped into by a member who is not the
    /// leader, <see cref="EnterFromRegionAsync"/>).</param>
    public async Task EnterAsync(GameSession s, EnterDungeonReq req, bool alone = false)
    {
        await DeliverPendingAsync(s);
        if (GuildChallengeEnter != null && await GuildChallengeEnter(s, req)) return;
        var plan = _catalog.Resolve(req);
        int error = plan == null ? ErrorCodes.DungeonDataMissing : Admission(s, plan, continuation: true);
        if (error == 0 && Preparing(s)) error = ErrorCodes.DungeonPreparing;
        var membership = s.Role != null ? _teams.TeamOf(s.Role.Id) : default;
        long[] ids = s.Role == null ? [] : alone || plan?.MaxPlayers == 1 ? [s.Role.Id] : [.. _teams.MembersOf(s.Role.Id)];
        var online = _sessions.All().Where(p => p.Role != null).ToDictionary(p => p.Role!.Id);
        var members = ids.Where(online.ContainsKey).Select(id => online[id]).ToArray();
        if (error == 0 && ids.Length > 1 && membership.Leader != s.Role!.Id) error = ErrorCodes.NoPermission;
        // GM "!dungeonsolo on" (test aid, not original behaviour): a lone role may also enter a team boss with 30400.
        if (error == 0 && ids.Length < plan!.MinPlayers && !(ids.Length == 1 && s.Instances.DungeonSoloMatch && plan.Multi != null))
            error = ErrorCodes.DungeonTeamSize;
        if (error == 0 && ids.Length > plan!.MaxPlayers) error = ErrorCodes.PartyFull;
        if (error == 0 && members.Length != ids.Length) error = ErrorCodes.PlayerOffline;
        if (error == 0)
            foreach (var member in members)
            {
                error = Preparing(member) ? ErrorCodes.DungeonPreparing : Admission(member, plan!, continuation: true);
                if (error != 0) break;
                // A continuation may include only members of this settled room, never a second party's instance.
                if (member.InstanceId != 0 && member.InstanceId != s.InstanceId) { error = ErrorCodes.DungeonBusy; break; }
            }
        // Reject server-owned run/team identities supplied by a forged request; zero is the direct Lua sender.
        var selected = req.InitData?.MultiDungeonInitData;
        if (error == 0 && selected != null && (selected.TeamID != 0 && selected.TeamID != membership.TeamId
            || selected.DungeonId != 0 && selected.DungeonId != s.InstanceId)) error = ErrorCodes.Parameter;
        await s.SendGameAsync(Enter, new EnterDungeonRsp { Result = error });
        if (error != 0) return;
        if (members.Length == 1) { await BeginAsync(plan!, members, s.Role!.Id, alone ? 0 : membership.TeamId); return; }
        var p = new Preparation { Plan = plan!, Members = members, Leader = s, TeamId = membership.TeamId };
        p.Ready.Add(s); // Initiating the request is the leader's affirmative answer (inferred).
        foreach (var member in members) _pending[member] = p;
        await BroadcastReadyAsync(p);
        int seconds = (int)(Data.Config.PublicParams.Of(_data).GetInPositionMaxTime ?? 30);
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(seconds), () => p.Closed ? Task.CompletedTask : CancelAsync(p, ErrorCodes.Parameter, 0));
    }

    /// <summary>
    /// A Valhalla Trials layer (ValhallaService owns its admission): the ordinary private-room engine runs the boss,
    /// the clear is a Records entry written with its reward (FreezeLockedAsync). 0 or the refusal to answer with.
    /// </summary>
    internal async Task<int> AdmitValhallaAsync(GameSession s, DungeonCatalog.Plan plan)
    {
        await DeliverPendingAsync(s);
        int error = Admission(s, plan);
        return error == 0 && Preparing(s) ? ErrorCodes.DungeonPreparing : error;
    }

    /// <summary>After <see cref="AdmitValhallaAsync"/> and the 83490 reply that closes the entry panel.</summary>
    internal Task BeginValhallaAsync(GameSession s, DungeonCatalog.Plan plan) => BeginAsync(plan, [s], s.Role!.Id, 0);

    /// <summary>The cleared Valhalla layers: Records keyed <c>valhalla:&lt;layer&gt;</c>.</summary>
    internal static IEnumerable<long> ValhallaCleared(State? state) => state == null ? []
        : state.Records.Where(r => r.Value.Passed && r.Key.StartsWith(ValhallaPrefix, StringComparison.Ordinal))
            .Select(r => long.Parse(r.Key.AsSpan(ValhallaPrefix.Length), System.Globalization.CultureInfo.InvariantCulture));

    internal const string ValhallaPrefix = "valhalla:";

    /// <summary>42033 is the actual prepare-panel producer (LC_DAL_Team.lua:3337-3587), not teleport invite 30118.</summary>
    private Task BroadcastReadyAsync(Preparation p, bool cancel = false, int error = 0, long role = 0)
    {
        var msg = new TeamEnterReadyQueueNotify
        {
            DungeonId = p.Plan.SceneEntry, DungeonInitData = p.Plan.Init(p.TeamId),
            Cancel = cancel, CancelErrorCode = error, CancelRoleId = role
        };
        foreach (var member in p.Members) if (member.Role != null) msg.ReadyQueue[member.Role.Id] = p.Ready.Contains(member);
        return Task.WhenAll(p.Members.Where(s => s.Role != null).Select(s => SendQuietAsync(s, ReadyQueue, msg)));
    }

    private async Task CancelAsync(Preparation p, int error, long role)
    {
        if (p.Closed) return;
        p.Closed = true;
        foreach (var member in p.Members) if (_pending.GetValueOrDefault(member) == p) _pending.Remove(member);
        await BroadcastReadyAsync(p, true, error, role);
    }

    public async Task ConfirmAsync(GameSession s, ConfirmTeamDungeonReq req)
    {
        var p = _pending.GetValueOrDefault(s);
        int error = p == null || p.Closed ? ErrorCodes.Parameter : Admission(s, p.Plan, continuation: true);
        if (error == 0 && (_teams.TeamOf(p!.Leader.Role!.Id) != (p.TeamId, p.Leader.Role.Id)
            || !_teams.MembersOf(p.Leader.Role.Id).Order().SequenceEqual(p.Members.Select(m => m.Role?.Id ?? 0).Order())))
            error = ErrorCodes.MemberLeft;
        await s.SendGameAsync(Confirm, new ConfirmTeamDungeonRsp { Result = error });
        if (p == null || p.Closed) return;
        if (error != 0 || !req.IsAgree) { await CancelAsync(p, error != 0 ? error : ErrorCodes.DungeonDeclined, s.Role!.Id); return; }
        p.Ready.Add(s);
        await BroadcastReadyAsync(p);
        if (p.Ready.Count != p.Members.Length) return;
        // Revalidate everyone after the last confirmation; no member can enter dead/offline/in another scene.
        foreach (var member in p.Members)
        {
            error = _sessions.Contains(member) ? Admission(member, p.Plan, continuation: true) : ErrorCodes.PlayerOffline;
            if (error != 0) { await CancelAsync(p, error, member.Role?.Id ?? 0); return; }
        }
        p.Closed = true;
        foreach (var member in p.Members) _pending.Remove(member);
        await BeginAsync(p.Plan, p.Members, p.Leader.Role!.Id, p.TeamId);
    }

    /// <summary>42047 reminds a member of this live 42033 ready vote (LC_DAL_Team:3641-3715).</summary>
    public async Task RemindAsync(GameSession s, TeamConfirmRemindersReq req)
    {
        var p = _pending.GetValueOrDefault(s);
        // PreparePanel passes (selfGuid, selectedUnreadyGuid) to TeamConfirmReminder.
        var target = p?.Members.FirstOrDefault(m => m.Role?.Id == req.RemindersGuid);
        int code = p == null || p.Closed || p.Leader != s || target == null || p.Ready.Contains(target)
            || req.PlayerId != s.Role?.Id || req.RemindersGuid == s.Role?.Id
            ? ErrorCodes.NoPermission : 0;
        await s.SendGameAsync(ConfirmReminder, new TeamConfirmRemindersResp { Result = code });
        if (code == 0) await SendQuietAsync(target!, ConfirmReminderNotify,
            new TeamConfirmRemindersNotify { MemberGuid = s.Role!.Id });
    }

    internal async Task BeginAsync(DungeonCatalog.Plan plan, GameSession[] members, long leader, long team, bool matched = false)
    {
        if (IsRandom(plan)) { await RollAsync(plan, members, leader, team, matched); return; } // DungeonService.HiddenRealm
        var room = _instances.Create(plan.SceneEntry, members);
        _praise.Remember(room.Id, plan.SceneEntry, members.Select(m => m.Role!.Id)); // MVP likes on its result screen
        // (inferred) 30107 acknowledges a real room allocation; this client build has no Lua handler beyond PB registration.
        foreach (var member in members)
            await SendQuietAsync(member, CreateResult, new CreateDungeonResultNotify());
        var a = new Attempt
        {
            Room = room, Plan = plan, Leader = leader, TeamId = team,
            PlayBook = _seasons.PlayBook, SeasonId = _seasons.SeasonId, Matched = matched,
            Blessing = plan.Boss is { } blessed ? Blessing(blessed)?.Row : null
        };
        foreach (var member in members)
        {
            a.Stats[member.Role!.Id] = new RoleStatisticsInfo
            {
                BaseInfo = new StatisticsRoleBaseInfo
                {
                    Id = member.Role.Id, Name = member.Role.Name, Job = member.Progress!.Career, Area = 1, TeamId = team,
                    RewardData = new StatisticsAwardCheckData { PlayerLevel = member.Progress.Level, Job = member.Progress.Career }
                }
            };
            a.StartingItems[member] = Inventory(member);
        }
        foreach (var member in members)
        {
            try
            {
                if (member.DungeonAttempt != null) await member.SendGameAsync(Reset, new DungeonResetNtf());
                if (await _instances.EnterAsync(room, member, members.Length > 1 ? 5 : 3))
                {
                    member.DungeonAttempt = a;
                    // (inferred) actual scene/room identities after successful handoff, never on a failed transfer.
                    await SendQuietAsync(member, EnterSceneResult, new ToSceneEnterDungeonResult
                    {
                        SceneId = plan.SceneEntry, DungeonId = room.Id,
                    });
                }
                else await _instances.AbandonAsync(room, member);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Dungeon transfer failed for {Role}, instance {Instance}", member.Role?.Id, room.Id);
                await _instances.AbandonAsync(room, member);
            }
        }
        a.Entering = false;
        if (ReadyToStart(a)) await StartAsync(a);
        // A client that never acknowledges scene loading cannot retain a room indefinitely (inferred timeout).
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(60), async () =>
        {
            foreach (var member in Present(a).Where(s => !a.Loaded.Contains(s))) await _instances.LeaveAsync(member);
        });
    }

    internal async Task LoadedAsync(GameSession s)
    {
        if (s.DungeonAttempt is not { } a || s.InstanceId != a.Room.Id || a.Room.Closed || !a.Loaded.Add(s)) return;
        await s.SendGameAsync(BaseInfo, Snapshot(a));
        TrackRun(a, [s], TrackerStep(a));
        await SendReviveInfoAsync(s, a);
        await SendRosterAsync(a);
        await RefreshBondsAsync(a);
        if (ReadyToStart(a)) await StartAsync(a);
    }

    /// <summary>
    /// The tracker step a run is at: the lair's wave (each wave is one of its dungeon tasks, DraculaWeddingService); a Guild
    /// Challenge stage is rebuilt as its boss fight, so its chain is at the last step, the boss (inferred).
    /// </summary>
    private static int TrackerStep(Attempt a) => a.Mode is { } mode ? mode.TrackerStep(a) : a.Settled ? TaskService.RunOver : a.Wave;

    internal void TrackRun(Attempt a, IEnumerable<GameSession> members, int step)
    {
        foreach (var s in members)
            foreach (var handler in RunTracked.Handlers) handler(s, a.Plan.SceneEntry, a.Room.Id, step);
    }

    // Waiting for all remaining members before starting the encounter clock is (inferred).
    private static bool ReadyToStart(Attempt a) => !a.Entering && !a.Started && !a.Settled
        && a.Mode?.WaitsForManualStart != true
        && a.Room.ActiveMembers.Count > 0 && a.Room.ActiveMembers.All(m => a.Loaded.Contains(m.Session));

    // Lua_GameDefine enum_DungeonMapStage: 0/1/2 initializing/running/settling; LC_DunGeon_Base:1507-1522
    // divides these wire timestamps by 1000. One level and sequential stages remain (inferred).
    internal DungeonBaseInfoNotify Snapshot(Attempt a)
    {
        var init = a.Plan.Init(a.TeamId, a.Room.Id);
        if (a.Matched && init.MultiDungeonInitData != null) init.MultiDungeonInitData.IsMatch = true;
        var snapshot = new DungeonBaseInfoNotify
        {
            DungeonEntry = a.Plan.SceneEntry, DungeonId = a.Room.Id, Stage = a.Wave + 1, Level = 1,
            InitData = init, ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            Vars = { PlayerList(a) },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = a.Settled ? 2 : a.Started ? 1 : 0,
                Stage = a.Wave + 1, MapStateStartTime = a.StartMs, StageStartTime = a.StartMs,
                MapStateEndTime = a.Started ? a.StartMs + a.Plan.DurationMs : 0,
                StageEndTime = a.Started ? a.StartMs + a.Plan.DurationMs : 0,
                NextStageStartTime = 0, NextStageEndTime = 0
            },
        };
        if (QuestList(a) is { } quests) snapshot.Vars.Add(quests);
        if (GoalParams(a) is { } goal) snapshot.Vars.Add(goal);
        a.Mode?.PopulateSnapshot(a, snapshot);
        if (FunctionButtons(a) is { } buttons) snapshot.Vars.Add(buttons);
        if (GuideWays(a) is { } ways) snapshot.Vars.Add(ways);
        if (Resurrections(a) is { } revives) snapshot.Vars.Add(revives);
        if (a.Preparing)
        {
            // (inferred) The preparation phase is the map's initializing state, ending when the boss fight begins.
            snapshot.StageModuleData.MapState = 0;
            snapshot.StageModuleData.MapStateEndTime = a.StartMs + _options.RaidPreparationSeconds * 1000L;
            snapshot.StageModuleData.StageEndTime = snapshot.StageModuleData.MapStateEndTime;
        }
        return snapshot;
    }

    private static DungeonVar PlayerList(Attempt a)
    {
        // LC_DunGeon_VarDefine:145 CDungeonPlayerUidList. Its startup use is (inferred), values are real members.
        var v = new DungeonVar { VarId = 145 };
        v.Vi64.Add(Present(a).Select(s => s.Role!.Id));
        return v;
    }

    /// <summary>
    /// LC_DunGeon_VarDefine 10001 PDungeonQuestIdList: the dungeon task widget shows the last id's DungeonQuestConfig
    /// title and goals (UI_Sys_Main_TeamUp_GVETaskWidgetBaseCtrl._refreshQuest). Sent when the current wave names its
    /// quest: every configured quest of the scene up to it, so earlier ones read as done (inferred).
    /// </summary>
    private DungeonVar? QuestList(Attempt a)
    {
        if (a.Wave >= a.Plan.Waves.Count || a.Plan.Waves[a.Wave].Quest is not (> 0 and var current)) return null;
        var v = new DungeonVar { VarId = 10001 };
        v.Vi64.Add(_data.Table("DungeonQuestConfig").Values
            .Where(q => GameData.Int(q, "_iDungeonID") == a.Plan.SceneEntry && GameData.Int(q, "_iQuestID") <= current)
            .Select(q => GameData.Int(q, "_iQuestID")).Distinct().Order());
        return v;
    }

    /// <summary>
    /// LC_DunGeon_VarDefine 10003 PDungeonQuestGoalParamList: a goal [type, i, j] shows params i+1 and j+1 as its
    /// "(done/total)" (Lua_CFG_DungeonQuestConfig._getQuestInfoByID). The story goals are [5,0,1]: this wave's kills
    /// and its size (inferred: the original server's counters are not recovered).
    /// </summary>
    internal static DungeonVar? GoalParams(Attempt a)
    {
        if (a.Wave >= a.Plan.Waves.Count || a.Plan.Waves[a.Wave].Quest <= 0) return null;
        int size = a.Plan.Waves[a.Wave].Spawns.Count;
        var v = new DungeonVar { VarId = 10003 };
        if (Step(a) is { } step && size == 0)
        {
            // A scripted step: its configured counter, or the events an "all" step has seen (inferred). A step that was
            // completed reads full, or the tracker keeps "Open the treasured chest (0/1)" until the scene ends.
            bool completed = a.GoalFullWave == a.Wave;
            v.Vi64.Add(step.All ? [a.StepSeen.Count, step.Complete.Count] : [completed ? step.Goal.Total : step.Goal.Done, step.Goal.Total]);
            return v;
        }
        v.Vi64.Add([Math.Clamp(a.Killed.Count - a.WaveBase, 0, size), size]);
        return v;
    }

    internal async Task SendQuietAsync(GameSession s, uint id, IMessage msg)
    {
        try { await s.SendGameAsync(id, msg); }
        catch (Exception e) { _log.LogWarning(e, "Dungeon notification {Message} failed for {Role}", id, s.Role?.Id); }
    }

    internal Task BroadcastAsync(Attempt a, uint id, IMessage msg) =>
        Task.WhenAll(Present(a).Where(a.Loaded.Contains).Select(s => SendQuietAsync(s, id, msg)));

    /// <summary>
    /// 30441 DungeonTeamInfoNotify. A nonzero teamId switches the client to its Virtual team (LC_DAL_VirtualTeam
    /// _processDungeonTeamInfoNotify, LC_TeamDataRouter), whose actions are 30442-30451; every group this engine runs is
    /// a TeamService party or no party, so the id is 0 (Default mode): the party HUD and the 42009/42012/42015/42031 team
    /// actions stay on the real team, as the _iConvene=1 dungeons (PVE_10P, FivePeopleDungeon, ...) imply (inferred).
    /// Positions are 0-based: the client shows position + 1 in slots 1..5 (UI_Sys_TeamFiveWidgetCtrl).
    /// </summary>
    private Task SendRosterAsync(Attempt a)
    {
        var roster = new DungeonTeamInfoNotify { TeamId = 0, LeaderGuid = a.Leader, TeamType = (DungeonTeamType)1 };
        var squad = new DungeonTeamSquadInfo { SquadIdx = 1, SquadLeaderGuid = a.Leader };
        foreach (var member in Present(a))
            squad.Members[member.Role!.Id] = new DungeonTeamMemberInfo { Guid = member.Role.Id, Position = Slot(a, member.Role.Id) };
        roster.Squads[1] = squad; // ordinary preformed roster, tactical reorganization is not implemented (inferred).
        return BroadcastAsync(a, TeamInfo, roster);
    }

    /// <summary>
    /// A member's 0-based roster slot, kept for the whole run: the party's own slot order when the run started (the
    /// leader's slot in it), else the leader first; later arrivals take the next slot (inferred).
    /// </summary>
    internal int Slot(Attempt a, long role)
    {
        if (a.Slots.Count == 0)
        {
            if (a.TeamId != 0 && _teams.TeamOf(a.Leader).TeamId == a.TeamId) a.Slots.AddRange(_teams.MembersOf(a.Leader));
            else a.Slots.Add(a.Leader);
        }
        int slot = a.Slots.IndexOf(role);
        if (slot >= 0) return slot;
        a.Slots.Add(role);
        return a.Slots.Count - 1;
    }

    internal async Task StartAsync(Attempt a)
    {
        if (a.Started || a.Settled || a.Room.Closed) return;
        a.Started = true; a.StartedAt = _loop.Now; a.StartMs = _loop.NowMs;
        if (StartBattleScenes.Contains(a.Plan.SceneEntry) && a.Plan.Multi != null)
        {
            // A Realm of the Gods raid opens with its preparation phase: the leader's Start Battle (30125) or the
            // configured time begins the fight (DungeonService.RealmOfGods).
            a.Preparing = true;
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
            _loop.ScheduleLegacy(TimeSpan.FromSeconds(Math.Max(0, _options.RaidPreparationSeconds)), () => EndPreparationAsync(a));
            return;
        }
        await BeginFightAsync(a);
    }

    internal async Task BeginFightAsync(Attempt a)
    {
        a.StartedAt = _loop.Now; a.StartMs = _loop.NowMs;
        await BroadcastAsync(a, BaseInfo, Snapshot(a));
        await SpawnWaveAsync(a);
        StartRaidTrials(a);
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(a.Plan.DurationMs), () => a.Settled || a.Room.Closed ? Task.CompletedTask : SettleAsync(a, false));
    }

    internal async Task SpawnWaveAsync(Attempt a)
    {
        if (a.Settled || a.Room.Closed) return;
        a.Spawning = true;
        a.WaveBase = a.Spawned;
        var change = new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry,
            EventType = (DungeonEventType)1, StageNum = a.Wave + 1, LevelNum = 1, Vars = { PlayerList(a) }
        };
        if (QuestList(a) is { } quests) change.Vars.Add(quests);
        if (GoalParams(a) is { } goal) change.Vars.Add(goal);
        a.Mode?.PopulateWaveEvent(a, change);
        await BroadcastAsync(a, Event, change);
        // Seen live: the task widget takes its vars from 30401 only, not from this event; a later wave whose
        // quest is known republishes the state so the tracker moves on (the first one follows StartAsync's).
        if (a.Wave > 0 && QuestList(a) != null) await BroadcastAsync(a, BaseInfo, Snapshot(a));
        if (a.Mode is { } mode) await mode.OnWaveSpawnedAsync(a);
        if (Step(a) is { } step)
        {
            await BeginStepAsync(a, step);
            if (a.Plan.Waves[a.Wave].Spawns.Count == 0)
            {
                a.Spawning = false;
                await AdvanceAsync(a); // waits for the step's client event
                return;
            }
            // A step with a fight spawns it below; it advances once its event and its kills are both in.
        }
        foreach (var spawn in a.Plan.Waves[a.Wave].Spawns)
        {
            if (a.Settled || a.Room.Closed) { a.Spawning = false; return; }
            var monster = await _instances.SpawnAsync(a.Room, spawn.NpcId, spawn.Point, grounded: spawn.Route == null);
            if (a.Settled || a.Room.Closed)
            {
                a.Spawning = false;
                await _instances.ClearMonstersAsync(a.Room);
                return;
            }
            if (monster == null) { a.Spawning = false; await SettleAsync(a, false); return; }
            a.Spawned++;
            if (spawn.Route is { } route)
            {
                await FlyAsync(a, monster, route);
                continue;
            }
            // Multi-bosses retain their AI instead of being ordered to attack on scene load. In Culvert the
            // born point 15 is 15m from monster point 19; NPC 4300000/1 uses non-roaming AI 25000001 and
            // RaidFive_Boss00[easy], whose server script is absent. Waiting for a player hit/taunt is an
            // (inferred) fallback, not proof that its 100m view/leash means automatic entry aggro.
            // No five-player preparation duration/start-button contract was recovered; GVE/Whale's
            // DungeonStart preparation UI belongs to other gameplay types. Story-wave engagement is unchanged.
            if (a.Plan.Multi == null && Present(a).FirstOrDefault(s => !s.Dead && !s.SceneLoading) is { } target)
                Scripted.EngageMonster(monster, target);
        }
        a.Spawning = false;
        await AdvanceAsync(a);
    }

    private async Task KilledAsync(GameSession s, MonsterUnit monster)
    {
        if (Active(s) is not { } a || monster.InstanceId != a.Room.Id || !a.Room.Monsters.Contains(monster)) return;
        bool precounted = a.Precounted.Remove(monster.Uid);
        if (!precounted && !a.Killed.Add(monster.Uid)) return;
        // A terminal freeze may still be awaiting its writer when this already-durable
        // actor publishes. Its count was added during precounting, even before Frozen flips.
        if (!precounted && !a.ObligationsFrozen)
        {
            var stats = a.Stats[s.Role!.Id];
            if (!stats.KillNpcData.TryGetValue(monster.Template.NpcId, out var count))
                stats.KillNpcData[monster.Template.NpcId] = count = new ExtraValueData();
            count.LongValue++;
        }
        // The tracker's "(done/total)" follows each kill of a wave that plays a known quest.
        if (!a.Settled && GoalParams(a) != null) // a precounted kill is already in Killed
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
        if (a.Precounted.Count == 0) await AdvanceAsync(a);
    }

    internal async Task AdvanceAsync(Attempt a)
    {
        if (a.Settled || a.Preparing || a.Spawning || a.Precounted.Count != 0 || a.Killed.Count != a.Spawned || a.Advancing) return;
        if (Step(a) != null && !a.StepDone) return;
        // A scripted step's hold: what it showed (a bubble being read, an enemy falling) is not cut by the next step or the
        // result. Only once per step, and not past the run's time limit.
        if (Step(a) is { HoldAfterMs: > 0 } step && !a.StepHoldOver && _loop.Now - a.StartedAt < TimeSpan.FromMilliseconds(a.Plan.DurationMs))
        {
            if (a.StepHeld) return; // the hold is running; its end advances
            a.StepHeld = true;
            _loop.ScheduleLegacy(Paced(step.HoldAfterMs), () =>
            {
                a.StepHoldOver = true;
                return a.Settled || a.Room.Closed ? Task.CompletedTask : AdvanceAsync(a);
            }, "dungeon scripted step hold");
            return;
        }
        a.Advancing = true;
        if (_loop.Now - a.StartedAt >= TimeSpan.FromMilliseconds(a.Plan.DurationMs)) await SettleAsync(a, false);
        else if (a.Wave + 1 == a.Plan.Waves.Count) await SettleAsync(a, true);
        else { a.Wave++; await SpawnWaveAsync(a); }
        a.Advancing = false;
        // Kills can arrive while asynchronous spawning is finishing; recheck the completed wave once.
        if (!a.Settled && a.Killed.Count == a.Spawned) await AdvanceAsync(a);
    }

    // Lua_GameDefine:8927-8934: 0 OnClickRevive, 1 AutoRevive, 2 NoRevive. Within OnClickRevive,
    // LC_SceneCom_Revive.lua:650-659,1165-1182 interprets [2,5] as an automatic request after five seconds.
    private static int AutoReviveDelay(Attempt a)
    {
        if (GameData.Int(a.Plan.Config, "_iReviveTrigger") != 0
            || !a.Plan.Config.TryGetProperty("_kTriggerParm", out var options)
            || options.ValueKind != JsonValueKind.Array) return 0;
        foreach (var option in options.EnumerateArray())
            if (option.ValueKind == JsonValueKind.Array && option.GetArrayLength() >= 2
                && GameData.Number(option[0]) == 2)
                return (int)Math.Clamp(GameData.Number(option[1]) ?? 0, 0, 3600);
        return 0;
    }

    // Lua_GameDefine.lua:8936-8952 names option 0 Immediately; the client's normal revive button sends 31013.
    private static bool ImmediateRevive(Attempt a)
    {
        if (a.Plan.Boss != null || GameData.Int(a.Plan.Config, "_iReviveTrigger") != 0
            || !a.Plan.Config.TryGetProperty("_kTriggerParm", out var options)
            || options.ValueKind != JsonValueKind.Array) return false;
        foreach (var option in options.EnumerateArray())
            if (option.ValueKind == JsonValueKind.Array && option.GetArrayLength() > 0
                && GameData.Number(option[0]) == 0) return true;
        return false;
    }

    // In particular 3120401 is NoRevive; a boss's initial count does not authorize self-revive there.
    // A Sacred Blessing level's _iAddReviveNum ("increases the team's Revive count in the Dungeon") adds to the boss's own.
    private static int ReviveLimit(Attempt a) => GameData.Int(a.Plan.Config, "_iReviveTrigger") == 0
        && a.Plan.Boss is { } boss ? Math.Max(0, (int)GameData.Int(boss, "_iOriginalReviveNum")) + BlessingRevives(a) : 0;
    private static bool Defeated(Attempt a) => !ImmediateRevive(a) && Present(a).All(p => p.Dead
        && !a.AutoReviveAt.ContainsKey(p.Role!.Id)
        && a.RevivesUsed.GetValueOrDefault(p.Role.Id) >= ReviveLimit(a));

    /// <summary>The run revives this role without using a count: OnClickRevive [0], or its timed [2,n] request pending.</summary>
    private static bool ReviveIsFree(Attempt a, long roleId) => ImmediateRevive(a) || a.AutoReviveAt.ContainsKey(roleId);

    // A NoRevive boss room (Phantom Realm, TenGameplayBossConfig) refuses the dead player's own revive (ErrorCodeConfig
    // 554 "Manual Revive is not allowed in this area"), not a teammate's: the boss's _iOriginalReviveNum plus the Divine
    // Blessing's _iAddReviveNum is the party's count (Localization_en: "increases the number of party Revives available
    // in the Dungeon"; ErrorCodeConfig 570 "Not enough party Revive attempts remaining"). The whole room shares it, one
    // count per revive (inferred).
    private static bool PartyRevives(Attempt a) => a.Plan.Boss != null && new DungeonConfig(a.Plan.Config).ReviveTrigger == NoRevive;
    private static int PartyRevivesLeft(Attempt a) => a.Plan.Boss is { } boss
        ? Math.Max(0, (int)new TenGameplayBossConfig(boss).OriginalReviveNum) + BlessingRevives(a) - a.RevivesUsed.Values.Sum() : 0;
    private const int NoRevive = 2; // Lua_GameDefine enum_ReviveTriggerType.NoRevive

    /// <summary>All resurrection sources obey the active dungeon's click-revive allowance, as ReviveAsync reads it, or in
    /// a NoRevive boss room the party's count.</summary>
    public int ExternalReviveError(GameSession s)
    {
        if (!s.Dead) return ErrorCodes.TargetAlive;
        if (s.DungeonAttempt == null) return 0;
        var a = Active(s);
        if (a == null || s.Role == null || a.Reviving.Contains(s)) return ErrorCodes.FeatureUnavailable;
        if (PartyRevives(a)) return PartyRevivesLeft(a) > 0 ? 0 : ErrorCodes.PartyRevivesUsedUp;
        return !ReviveIsFree(a, s.Role.Id) && a.RevivesUsed.GetValueOrDefault(s.Role.Id) >= ReviveLimit(a)
            ? ErrorCodes.FeatureUnavailable : 0;
    }

    /// <summary>Call exactly once after a successful skill/item resurrection in an active run.</summary>
    public void RecordExternalRevive(GameSession s)
    {
        if (s.DungeonAttempt == null) return;
        var a = Active(s);
        if (a == null || s.Role == null) return;
        if (!ReviveIsFree(a, s.Role.Id)) a.RevivesUsed[s.Role.Id] = a.RevivesUsed.GetValueOrDefault(s.Role.Id) + 1;
        a.AutoReviveAt.Remove(s.Role.Id); // or the next death keeps this one's time (PlayerDiedAsync TryAdd)
    }

    public async Task PublishExternalReviveAsync(GameSession s)
    {
        if (s.Role == null || Active(s) is not { } a) return;
        await SendReviveInfoAsync(s, a);
        if (Resurrections(a) is { } revives)
            await BroadcastAsync(a, Event, new DungeonEventNotify
            {
                DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)4, StageNum = a.Wave + 1, LevelNum = 1, Vars = { revives },
            });
    }

    /// <summary>LC_DunGeon_VarDefine 140 CNumberOfResurrections: the party's count left, which the HUD shows as
    /// "Revives Remaining: ${1}" (UI_Sys_Main_Team_ResurrectionsWidget, UIWords 56126). Without it the HUD keeps its
    /// prefab's "10".</summary>
    private static DungeonVar? Resurrections(Attempt a) => PartyRevives(a)
        ? new DungeonVar { VarId = ResurrectionsVar, Vi64 = { PartyRevivesLeft(a) } } : null;
    private const int ResurrectionsVar = 140;

    private async Task PlayerDiedAsync(GameSession s)
    {
        if (Active(s) is not { } a) return;
        int delay = AutoReviveDelay(a);
        if (delay > 0) a.AutoReviveAt.TryAdd(s.Role!.Id, _clock.GetUtcNow().ToUnixTimeSeconds() + delay);
        await SendReviveInfoAsync(s, a);
        if (Defeated(a)) await SettleAsync(a, false);
    }

    /// <summary>
    /// NoRevive and out-of-scope top-level automatic/script modes refuse self-revival. OnClickRevive [0] admits
    /// immediate normal-button requests; [2,5] admits the client's timed automatic request. Finite boss counts
    /// retain their existing precedence and are per participant
    /// (inferred; shared-pool scope is unproven).
    /// Default 31013 uses the existing map resurrection point/full HP (inferred); arbitrary point/script requests
    /// are refused. A party loses when all remaining players are dead and out of revivals (inferred).
    /// </summary>
    public async Task ReviveAsync(GameSession s, EntityReliveReq req)
    {
        var a = Active(s);
        long roleId = s.Role?.Id ?? 0;
        long autoAt = a?.AutoReviveAt.GetValueOrDefault(roleId) ?? 0;
        int error = !s.Dead ? ErrorCodes.TargetAlive : a == null || s.Role == null ? ErrorCodes.FeatureUnavailable
            : req.ReviveParam != 0 || (int)req.Type != 0 || (int)req.ReqType != 0 ? ErrorCodes.Parameter
            : a.Reviving.Contains(s) || (autoAt > 0
                ? _clock.GetUtcNow().ToUnixTimeSeconds() < autoAt
                : !ImmediateRevive(a) && a.RevivesUsed.GetValueOrDefault(roleId) >= ReviveLimit(a))
                ? ErrorCodes.FeatureUnavailable : 0;
        if (error == 0 && !_combat.TryClaimRevive(s)) error = s.Dead ? ErrorCodes.FeatureUnavailable : ErrorCodes.TargetAlive;
        if (error != 0)
        {
            await s.SendGameAsync(CombatService.Relive, new EntityReliveAck { Result = error, EntityID = (ulong)(s.Role?.Id ?? 0) });
            return;
        }
        a!.Reviving.Add(s);
        bool counted = autoAt == 0 && !ImmediateRevive(a);
        try
        {
            await _combat.ReviveClaimedAsync(s, () =>
            {
                if (counted) a.RevivesUsed[roleId] = a.RevivesUsed.GetValueOrDefault(roleId) + 1;
                a.AutoReviveAt.Remove(roleId);
            });
        }
        finally
        {
            a.Reviving.Remove(s);
        }
        await SendReviveInfoAsync(s, a);
    }

    private Task SendReviveInfoAsync(GameSession s, Attempt a) => a.Plan.Multi == null && !s.Dead ? Task.CompletedTask
        : SendQuietAsync(s, ReviveInfo, new SyncClientPlayerReviveInfoNtf
        {
            BDead = s.Dead,
            // LC_SceneCom_Revive.lua:254-261 explicitly interprets freeReviveNum as USED, not remaining.
            FreeReviveNum = a.RevivesUsed.GetValueOrDefault(s.Role!.Id),
            ReviveTime = a.AutoReviveAt.GetValueOrDefault(s.Role.Id), SceneEntry = a.Plan.SceneEntry
        });

    public async Task ClientEventAsync(GameSession s, ClientDungeonEvent req)
    {
        if (s.DungeonAttempt?.Mode is { } mode && await mode.TryHandleClientEventAsync(s, req))
        {
            await s.SendGameAsync(ClientEvent, new NullArg());
            return;
        }
        // LC_DAL_Dungeon.lua:69-87: only control events with no client-authored rewards/functions are accepted.
        // Leader-only surrender and reset-after-settlement into a fresh room are (inferred control policy).
        if (s.DungeonAttempt is { } a && s.InstanceId == a.Room.Id && !s.SceneLoading && s.Role!.Id == a.Leader)
        {
            if (req.StrCmd == "DungeonGiveUp" && !a.Settled) await SettleAsync(a, false);
            else if (req.StrCmd == "DungeonReset" && a.Settled)
                await EnterAsync(s, new EnterDungeonReq { SceneEntry = a.Plan.SceneEntry, InitData = a.Plan.Init() });
            // DungeonStart / timeline / unknown commands cannot advance combat or produce a success.
        }
        await s.SendGameAsync(ClientEvent, new NullArg());
    }

    public async Task LeaveAsync(GameSession s)
    {
        var role = s.Role;
        var room = _instances.Find(s.InstanceId);
        var member = room?.MemberFor(s);
        bool valid = role != null && !s.SceneLoading && room != null && member != null
            && s.SceneEntry == room.SceneEntry;
        var attempt = s.DungeonAttempt;
        // Only an owned ordinary/multi run opts into inferred recovery; other private-room owners retain
        // their existing exit semantics. A stale dungeon attempt cannot turn another room's exit into a revive.
        bool dungeonReturn = valid && (attempt != null && attempt.Mode?.SuppressesDungeonReturn != true
            && ReferenceEquals(attempt.Room, room) || attempt == null && RecoverDeadOnLeave?.Invoke(room!.Id) == true);
        await s.SendGameAsync(Leave, new LeaveDungeonRsp { Result = valid ? 0 : ErrorCodes.SceneMissing });
        if (!valid || !ReferenceEquals(s.Role, role) || s.SceneLoading || s.InstanceId != room!.Id
            || s.SceneEntry != room.SceneEntry || !ReferenceEquals(_instances.Find(room.Id), room)
            || room.MemberFor(s) != member || !ReferenceEquals(s.DungeonAttempt, attempt)) return;
        await _instances.LeaveAsync(s, recoverDeadOnReturn: dungeonReturn);
    }

    /// <summary>
    /// Disconnect/exit cancels a pending vote and detaches this participant. Remaining members may continue below
    /// the entry minimum; reconnect returns outside and does not rejoin the run (inferred abandonment policy).
    /// </summary>
    public async Task AbandonAsync(GameSession s)
    {
        if (_pending.TryGetValue(s, out var p)) await CancelAsync(p, ErrorCodes.MemberLeft, s.Role?.Id ?? 0);
        if (s.DungeonAttempt is not { } a) return;
        s.DungeonAttempt = null; s.Attack = null;
        try
        {
            if (a.Mode is { } mode) await mode.OnPlayerLeftAsync(a, s);
            await ClearBondsAsync(s, a);
            await _instances.AbandonAsync(s);
        }
        // A full queue wrote nothing: the attempt stays for the handover's retry, which would otherwise find nothing to do.
        catch (Exception e) when (Accounts.Persistence.QueueFullPatience.Refused(e))
        {
            s.DungeonAttempt = a;
            throw;
        }
        var remaining = Present(a);
        if (remaining.Length == 0) { a.Settled = true; return; }
        await RefreshBondsAsync(a);
        await SendRosterAsync(a);
        if (ReadyToStart(a)) await StartAsync(a);
        // A retry of this leave would not come back here, so a refused settlement for the others waits for room now; it
        // reserves nothing before its freeze commits, so it can run again.
        else if (!a.Settled && Defeated(a)) await Accounts.Persistence.QueueFullPatience.RunAsync(() => SettleAsync(a, false));
    }

    // A scripted scene logs each walk/interaction step as it completes; its last fight step ends here, at the clear.
    [LoggerMessage(Level = LogLevel.Information, Message = "Dungeon {Scene} instance {Instance} settled (cleared: {Cleared}) at step {Step}/{Steps}")]
    private static partial void LogSettled(ILogger logger, long scene, long instance, bool cleared, int step, int steps);

    /// <summary>
    /// All configured actors dead before timeout wins (inferred predicate). Reserve once before any await.
    /// Each role's earned personal award and allowance are one SQLite transaction; disconnect is not a clear.
    /// </summary>
    internal async Task SettleAsync(Attempt a, bool won)
    {
        if (a.Settled || a.Room.Closed) return;
        if (a.Mode is { } mode)
        {
            a.Settled = true;
            await _instances.ClearMonstersAsync(a.Room);
            if (mode.ClearsSceneActorsOnSettle)
            {
                a.SceneActors.Clear(); // removed with the room's monsters
                a.Companions.Clear();
            }
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
            TrackRun(a, LoadedMembers(a), TaskService.RunOver);
            await mode.SettleAsync(a, won);
            return;
        }
        if (a.ObligationsFrozen && !a.ObligationsActivated)
            if (!await ResolvePreparedAsync(a)) won = false;
        await FreezeAsync(a, won);
        won = a.FrozenWin;
        a.Settled = true;
        long scene = a.Plan.SceneEntry, instance = a.Room.Id;
        int steps = a.Plan.Waves.Count, step = Math.Min(a.Wave + 1, steps);
        LogSettled(_log, scene, instance, won, step, steps);
        var participants = Present(a).Where(a.Loaded.Contains).Select(s => (Session: s, Role: s.Role!.Id)).ToArray();
        var notifications = new List<Func<Task>>();
        var auctionAwards = new List<ItemBaseInfo>();
        // A scripted scene's defeated enemies keep their bodies for their own remain time instead of vanishing with the result
        // (the Eddga's body stays where it fell); the living ones and the scene actors are cleared as before.
        await _instances.ClearMonstersAsync(a.Room, keepBodies: a.Plan.Waves.Any(w => w.Step != null));
        a.SceneActors.Clear(); // removed with the room's monsters
        a.Companions.Clear();
        foreach (var (s, roleId) in participants)
        {
            if (s.Role?.Id != roleId) continue;
            s.Attack = null;
            var sourceKey = $"dungeon/{_character.RewardRealmId}/{a.Room.Id}/{roleId}";
            var row = _obligations.Pending(roleId).SingleOrDefault(p => p.Key == sourceKey);
            if (row == null) throw new InvalidOperationException("Frozen dungeon recipient obligation is missing.");
            bool success = row.Success;
            var goods = row.Goods;
            var awardedItems = new List<ItemBaseInfo>();
            bool newRecord = row.NewRecord;
            bool committed = false;
            if (!a.Paid.Contains(s))
            {
                try
                {
                    var delivered = await DeliverAsync(s, row);
                    committed = delivered.Committed;
                    awardedItems = delivered.Awarded;
                    if (committed) a.Paid.Add(s);
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Dungeon settlement failed for {Role}, instance {Instance}", roleId, a.Room.Id);
                    if (s.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired)
                    {
                        s.CancelPendingCharacterIntents();
                        s.AbortForRecovery();
                    }
                    // The frozen outcome remains a win (or a loss) with a pending recipient delivery.
                    // Do not publish a contradictory terminal result while its economic source is unresolved.
                    continue;
                }
            }
            if (!committed) continue;
            if (committed && success)
            {
                foreach (var handler in Completed.Handlers)
                    try { await handler(s, (int)a.Plan.SceneEntry, goods.Count > 0); }
                    catch (Exception e) { _log.LogError(e, "Dungeon clear callback failed for {Role}, instance {Instance}", roleId, a.Room.Id); }
                if (a.Plan.Boss is { } boss)
                {
                    var party = participants.Select(p => p.Session).ToArray();
                    foreach (var handler in MultiBossCleared.Handlers)
                        try { await handler(s, GameData.Int(boss, "_iBoss"), party); }
                        catch (Exception e) { _log.LogError(e, "Dungeon boss clear callback failed for {Role}, instance {Instance}", roleId, a.Room.Id); }
                }
            }
            var result = new DungeonResNotify { DungeonEntry = a.Plan.SceneEntry, Res = (DungeonRes)(success ? 1 : 2) };
            if (success)
            {
                foreach (var group in goods.GroupBy(g => g.Entry)) result.Rewards[group.Key] = group.Sum(g => (long)g.Count);
                // Story rewards belong to existing NPC/task pipelines; report only actual item/currency deltas.
                if (a.Plan.Multi == null)
                    foreach (var (entry, count) in Inventory(s))
                        if (count > a.StartingItems[s].GetValueOrDefault(entry)) result.Rewards[entry] = count - a.StartingItems[s].GetValueOrDefault(entry);
            }
            notifications.Add(async () =>
            {
                if (s.Role?.Id != roleId) return;
                try
                {
                    if (committed && goods.Count > 0) await _character.ItemsChangedAsync(s, stats: false);
                    await s.SendGameAsync(Result, result);
                    if (success) await HideGuideWaysAsync(s, a);
                    if (success && (LeavesAfterClear(a) || _catalog.ScriptedLeavesAfterClear(a.Plan.SceneEntry))) ScheduleLeaveAfterClear(s, a);
                    else if (success && ContinuationOf(a.Plan.SceneEntry) is long nextScene and not 0) ScheduleContinuation(s, a, nextScene);
                    if (success && a.Plan.Multi != null)
                    {
                        var award = new SyncMultiDungeonAwardNotify
                        {
                            PassTime = row.DurationMs, IsFastRecord = newRecord,
                            Honor = EarnedHonors(row)
                        };
                        award.NormalAward.Add(awardedItems);
                        award.AutionAward.Add(auctionAwards);
                        await s.SendGameAsync(AwardNotify, award);
                    }
                    if (committed) await SyncAsync(s);
                }
                catch (Exception e) { _log.LogWarning(e, "Dungeon result delivery failed for {Role}, instance {Instance}", roleId, a.Room.Id); }
            });
        }
        if (won && a.Plan.Multi != null && a.Plan.Boss is { } cleared)
            RecordFirstClear(a, cleared, [.. participants.Select(p => p.Session).Where(s => s.Role != null)]);
        // Finalize only from committed personal entitlements; pending journals recover on restart.
        if (won && a.Plan.Multi != null && _obligations.PendingRoom(a.Room.Id).Count == 0)
        {
            try
            {
                await _auctions.MarkDungeonRunReadyAsync(a.Room.Id);
                await _auctions.FinalizeDungeonRunAsync(a.Room.Id);
                foreach (var group in _auctions.DungeonRows(a.Room.Id).GroupBy(r => (r.Entry, r.Bind)))
                    auctionAwards.Add(_character.ItemInfoOf(new InvItem
                    { Entry = group.Key.Entry, Stack = group.Count(), BindType = group.Key.Bind }));
            }
            catch (Exception e)
            {
                // Personal rewards already committed. The durable run journal lets the sweep retry auction
                // publication; it must not suppress the dungeon result/normal award notifications.
                _log.LogError(e, "Auction publication deferred after committed dungeon {Instance}", a.Room.Id);
            }
        }
        // A disconnected member's channel must never prevent another participant's committed award.
        foreach (var notify in notifications) await notify();
    }
}
