using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.TerritoryWarsService;

namespace Ro3Emu.Server.Game;

/// <summary>
/// War machines (cannons, vehicles, repair zones, summon platforms), battle rooms, commander marks, and command skills
/// for Territory Wars and Royal City Siege. Extracted from <see cref="TerritoryWarsService"/>.
/// </summary>
public sealed partial class WarMachineService
{
    private readonly TerritoryWarsService _territory;
    private readonly TeamService _teams;
    private readonly World _world;
    private readonly MonsterService _monsters;

    public WarMachineService(TerritoryWarsService territory, TeamService teams, World world, MonsterService monsters)
    {
        _territory = territory;
        _teams = teams;
        _world = world;
        _monsters = monsters;
        _scene.Leaving.Subscribe(CannonLeavingAsync);
        _scene.Leaving.Subscribe(VehicleLeavingAsync);
        _combat.PlayerDied.Subscribe(CannonDiedAsync);
        _combat.PlayerDied.Subscribe(VehicleDiedAsync);
        _combat.PlayerDied.Subscribe(PlatformLeavingAsync);
        _scene.Leaving.Subscribe(PlatformLeavingAsync);
        _world.PlayerEntered.Add(PlatformActivatorSeenAsync);
        _combat.GrantedSkill.Subscribe(SeatSkill);
        _combat.AfterLethalPublication.Subscribe(MachineDestroyedAsync);
        _combat.Killed.Subscribe(MachineKilledAsync);
        SiegeSettled.Subscribe(SiegeMachinesSettled);
        _world.PlayerEntered.Add(VehicleRiderSeenAsync);
        _combat.EventDamageRecorded.Subscribe(MachineDamageRecordedAsync);
        _guilds.WarSideOf = WarSide;
        _guilds.WarMachines = new MachineSite(this);
    }

    private GameData _data => _territory.GameData;
    private GuildService _guilds => _territory.Guilds;
    private TerritoryWarsClock _clock => _territory.Clock;
    private SessionRegistry _sessions => _territory.Sessions;
    private InstanceService _instances => _territory.Instances;
    private SceneService _scene => _territory.Scene;
    private WorldData _maps => _territory.Maps;
    private BuffService _buffs => _territory.Buffs;
    private CombatService _combat => _territory.Combat;
    private GameLoop _loop => _territory.Loop;
    private SemaphoreSlim _gate => _territory.Gate;
    private WorldState state => _territory.CurrentState;
    private SyncHook<long, long, bool> SiegeSettled => _territory.SiegeSettled;

    private IDisposable StageState() => _territory.StageState();
    private Task<Ownership.LegacyOwnerScope?> EnterGateAsync(string origin, GameSession? actor = null,
        IReadOnlyCollection<long>? additionalGuilds = null, bool includeParticipants = false) =>
        _territory.EnterGateAsync(origin, actor, additionalGuilds, includeParticipants);
    private long OwnId(GameSession s) => _territory.OwnId(s);
    private Task Send(GameSession s, uint id, IMessage msg) => _territory.Send(s, id, msg);
    private Task SaveAsync(WorldState value, string kind = "state", GameSession? actor = null, string? sourceKey = null) =>
        _territory.SaveAsync(value, kind, actor, sourceKey);
    private bool SiegeMember(GameSession s, SiegeRun battle) => _territory.Siege.SiegeMember(s, battle);
    private bool SiegeActive(SiegeRun battle) => _territory.Siege.SiegeActive(battle);
    private bool Active(Battle b) => _territory.Active(b);
    private bool Member(GameSession s, Battle b) => _territory.Member(s, b);
    private long NextScenePointUid() => _territory.NextScenePointUid();
    private Task SetScenePointAsync(Battle b, int pointId, EnumGlobalScenePoint status, long readyTime, long owner) =>
        _territory.SetScenePointAsync(b, pointId, status, readyTime, owner);
    private static IReadOnlyDictionary<int, ScenePoint> ScenePointsOf(BattleState r) => TerritoryWarsService.ScenePointsOf(r);
    private static IEnumerable<GameSession> LoadedMembers(Battle b) => TerritoryWarsService.LoadedMembers(b);
}
