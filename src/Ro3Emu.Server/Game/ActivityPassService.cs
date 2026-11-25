using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season battle pass, pass play EXP/challenges, month card, and cash-shop orders/first-recharge awards.
/// Extracted from <see cref="NewActivityService"/>.
/// </summary>
public sealed partial class ActivityPassService
{
    private readonly NewActivityService _activities;
    private readonly MailService _mail;
    private readonly TeamService _teams;
    private readonly bool _freePremiumPass, _passPlay, _freeProducts;
    private readonly int _freeTopUpsPerDay;

    public ActivityPassService(NewActivityService activities, MailService mail, CombatService combat, DungeonService dungeons,
        MvpService mvps, ChatService chat, TeamService teams, IOptions<ServerOptions> options)
    {
        _activities = activities;
        _mail = mail;
        _teams = teams;
        _freePremiumPass = options.Value.Pass.FreePremium;
        _passPlay = options.Value.Pass.Play;
        _freeProducts = options.Value.Shop.FreeProducts;
        _freeTopUpsPerDay = options.Value.Shop.FreeTopUpsPerDay;
        if (_passPlay) InitializePassPlay(combat, dungeons, mvps, _tasks, chat);
    }

    private GameData _data => _activities.GameData;
    private CharacterService _character => _activities.Character;
    private RoleStore _roles => _activities.Roles;
    private TimeProvider _clock => _activities.Clock;
    private ActivityClock _activityClock => _activities.ActivityClock;
    private AwardService _awards => _activities.Awards;
    private TaskService _tasks => _activities.Tasks;
    private GameLoop _loop => _activities.Loop;
    private Deadlines _deadlines => _activities.Deadlines;
    private ILogger _log => _activities.Log;
    private long Now => _activities.Now;

    private int Playbook(GameSession session) => _activities.Playbook(session);
    private int ActivityError(GameSession session, long activityId, int expectedType = 0, bool allowInitializing = false) =>
        _activities.ActivityError(session, activityId, expectedType, allowInitializing);
    private static ActivityRuntimeState CloneRuntime(GameSession session) => NewActivityService.CloneRuntime(session);
    private Task ReconcileAsync(GameSession session) => _activities.ReconcileAsync(session);
    private Task SyncAsync(GameSession session) => _activities.SyncAsync(session);
    private Task<LegacyOwnerScope> EnterLocalWithGateAsync(GameSession session, string origin,
        CommonActivityReq? request = null, long? loadedEveCycle = null, bool? loadedEveEnded = null) =>
        _activities.EnterLocalWithGateAsync(session, origin, request, loadedEveCycle, loadedEveEnded);
    private GameSession? AttachedSocialSession(RoleOwner owner) => _activities.AttachedSocialSession(owner);
    private Task<RoleOwner?> PinSocialRecipientAsync(long roleId) => _activities.PinSocialRecipientAsync(roleId);
    private EconomyAsset AdventureAsset(int entry) => _activities.Adventures.AdventureAsset(entry);
}
