using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Obligations = Ro3Emu.Server.Game.TimedBattleService.Obligations;
using State = Ro3Emu.Server.Game.TimedBattleService.State;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Rift Crisis stones and Commendation Vault (OnePiece) private boss fights (83350/83351, 30400/42033/30406).
/// Extracted from <see cref="TimedBattleService"/>.
/// </summary>
public sealed partial class RiftBattleService
{
    public const int RiftFunction = 95;
    private const uint OnePieceInfo = TimedBattleService.OnePieceInfo;
    private const uint OnePieceMap = TimedBattleService.OnePieceMap;

    private readonly TimedBattleService _timed;
    private readonly LifeJobService _life;
    private readonly MonsterService _monsters;
    private readonly World _world;
    private readonly TestingSwitches _testing;

    public RiftBattleService(TimedBattleService timed, LifeJobService life, MonsterService monsters, World world,
        TestingSwitches testing)
    {
        _timed = timed;
        _life = life;
        _monsters = monsters;
        _world = world;
        _testing = testing;
        _life.ProjectedRestrictions.Subscribe(RiftRestriction);
        _combat.Killed.Subscribe(OnePieceKilledAsync);
        _combat.PlayerDied.Subscribe(OnePieceDiedAsync);
    }

    private GameData _data => _timed.GameData;
    private TimedTodoSchedule _schedule => _timed.Schedule;
    private TimeProvider _time => _timed.Time;
    private RoleStore _roles => _timed.Roles;
    private AccountStore _accounts => _timed.AccountStore;
    private CharacterService _character => _timed.Character;
    private FunctionService _functions => _timed.Functions;
    private AwardService _awards => _timed.Awards;
    private TaskService _tasks => _timed.Tasks;
    private InstanceService _instances => _timed.Instances;
    private SceneService _scene => _timed.Scene;
    private WorldData _worldData => _timed.WorldData;
    private CombatService _combat => _timed.Combat;
    private SessionRegistry _sessions => _timed.Sessions;
    private TeamService _teams => _timed.Teams;
    private GameLoop _loop => _timed.Loop;
    private ILogger _log => _timed.Log;
    private HashSet<long> _pendingDeliveryRoles => _timed.PendingDeliveryRoles;
    private Task DeliverOneAsync(GameSession session, TimedBattleObligationRow obligation) => _timed.DeliverOneAsync(session, obligation);

    internal void AddLeaveKeys(GameSession session, List<Ownership.ReservationKey> keys)
    {
        if (_treasure.TryGetValue(session, out var run))
            keys.Add(Ownership.ReservationKey.Entity("timed_onepiece", run.Room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal void LeaveTreasureRole(GameSession session)
    {
        if (_treasure.TryGetValue(session, out var run))
        {
            run.Settled = true;
            _treasure.Remove(session);
        }
    }
}
