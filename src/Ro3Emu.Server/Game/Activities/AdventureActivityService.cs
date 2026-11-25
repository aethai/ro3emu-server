using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Adventure activities (Season Pre / Eve / Journey / Fragment Gather / Treasure Hunt / World Exploration) and world
/// exploration conches / Morroc pillars. Extracted from <see cref="NewActivityService"/>.
/// </summary>
public sealed partial class AdventureActivityService
{
    private const uint Common = NewActivityService.Common;
    private const uint Notify = NewActivityService.Notify;
    private const long EveWriteLeadSeconds = NewActivityService.EveWriteLeadSeconds;

    private readonly NewActivityService _activities;
    private readonly AccountStore _accounts;
    private readonly MailService _mail;
    private readonly SceneService _scene;
    private readonly Maps.WorldData _worldData;
    private readonly DungeonService _dungeons;
    private readonly ILogger _log;

    public AdventureActivityService(NewActivityService activities, AccountStore accounts, ItemStore items, MailService mail,
        SceneService scene, Maps.WorldData worldData, DungeonService dungeons, CombatService combat)
    {
        _activities = activities;
        _accounts = accounts;
        _mail = mail;
        _scene = scene;
        _worldData = worldData;
        _dungeons = dungeons;
        _log = activities.Log;

        dungeons.JourneyWon = JourneyDungeonWonAsync;
        dungeons.CanStageJourneyVictory = session => ActivityError(session, 112, 15) == 0;
        InitializeAdventureServices(accounts, items);
        _character.StartTreasureMap = StartTreasureMapAsync;
        combat.Killed.Subscribe(OnTreasureMonsterKilledAsync);
        combat.EventDamageRecorded.Subscribe(OnTreasureMonsterDamagedAsync);
        scene.TreasureComplete = CompleteTreasureHuntAsync;
        scene.Loaded.Subscribe(OnTreasureSceneLoadedAsync);
        combat.PlayerDied.Subscribe(InterruptConchAsync);
        scene.AfterTeleport = ConchMovedAsync;
        scene.Loaded.Subscribe(ReloadConchesAsync);
        scene.Resync.Subscribe(ReloadConchesAsync);
        scene.Leaving.Subscribe(LeaveConchesAsync);
        _tasks.QuestDelivered.Subscribe(OnExplorationQuestDelivered);
    }

    private GameData _data => _activities.GameData;
    private CharacterService _character => _activities.Character;
    private FunctionService _functions => _activities.Functions;
    private RoleStore _roles => _activities.Roles;
    private SeasonService _seasons => _activities.Seasons;
    private TimeProvider _clock => _activities.Clock;
    private AwardService _awards => _activities.Awards;
    private TaskService _tasks => _activities.Tasks;
    private SessionRegistry _sessions => _activities.Sessions;
    private GameLoop _loop => _activities.Loop;
    private Deadlines _deadlines => _activities.Deadlines;
    private long Now => _activities.Now;

    private int Playbook(GameSession session) => _activities.Playbook(session);
    private void RefreshCalendar(GameSession session, DBNewActivityInfo state) => _activities.RefreshCalendar(session, state);
    private int ActivityError(GameSession session, long activityId, int expectedType = 0, bool allowInitializing = false) =>
        _activities.ActivityError(session, activityId, expectedType, allowInitializing);
    private static ActivityRuntimeState CloneRuntime(GameSession session) => NewActivityService.CloneRuntime(session);
    private Task SyncAsync(GameSession session) => _activities.SyncAsync(session);
    private void QueueRefresh(GameSession session) => _activities.QueueRefresh(session);
    private static ReservationKey EveKey(long cycle, bool writes) => NewActivityService.EveKey(cycle, writes);
    private Task<LegacyOwnerScope> EnterLocalWithGateAsync(GameSession session, string origin,
        CommonActivityReq? request = null, long? loadedEveCycle = null, bool? loadedEveEnded = null) =>
        _activities.EnterLocalWithGateAsync(session, origin, request, loadedEveCycle, loadedEveEnded);
    private Task<LegacyOwnerScope> EnterSocialWithGateAsync(GameSession sender, long recipient, string origin, long ask = 0) =>
        _activities.EnterSocialWithGateAsync(sender, recipient, origin, ask);
    private GameSession? AttachedSocialSession(RoleOwner owner) => _activities.AttachedSocialSession(owner);
    private Task<RoleOwner?> PinSocialRecipientAsync(long roleId) => _activities.PinSocialRecipientAsync(roleId);
}
