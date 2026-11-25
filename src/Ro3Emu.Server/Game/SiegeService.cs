using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.TerritoryWarsService;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Royal City Siege (scene 2230101): schedule, siege rooms, stages, castle objects, walls, ladders/rams, bag NPCs,
/// siege vehicles, siege tags, and siege settlement. Extracted from <see cref="TerritoryWarsService"/>.
/// </summary>
public sealed partial class SiegeService
{
    private const uint SiegeBaseQuery = TerritoryWarsService.SiegeBaseQuery;
    private const uint SiegeHistoryQuery = TerritoryWarsService.SiegeHistoryQuery;
    private const uint SiegeResultNotify = TerritoryWarsService.SiegeResultNotify;
    private const uint SiegeToolAdd = TerritoryWarsService.SiegeToolAdd;
    private const uint GlobalObjectNotify = TerritoryWarsService.GlobalObjectNotify;
    private const long CheckCamp = TerritoryWarsService.CheckCamp;
    private const long GvgPermission = TerritoryWarsService.GvgPermission;

    private readonly TerritoryWarsService _territory;
    private readonly TimedTodoSchedule _siegeSchedule;
    private readonly MonsterService _monsters;
    private readonly MailService _mail;
    private readonly ILogger<SiegeService> _log;

    private string _siegePublishedState = "";
    private readonly ConditionalWeakTable<GameSession, StrongBox<long>> _siegePublishedGuild = [];

    public SiegeService(TerritoryWarsService territory, TimedTodoSchedule siegeSchedule, MonsterService monsters,
        MailService mail, ILogger<SiegeService> log)
    {
        _territory = territory;
        _siegeSchedule = siegeSchedule;
        _monsters = monsters;
        _mail = mail;
        _log = log;
        _scene.Loaded.Subscribe(SiegeLoadedAsync);
        _scene.Leaving.Subscribe(SiegeLeavingAsync);
        _scene.WalkBlocked = SiegeWalkBlocked;
        _scene.WalkBridged = SiegeWalkBridged;
        _monsters.Spares = SiegeSpares;
        _combat.Killed.Subscribe(SiegeKilledAsync);
        _combat.EventDamageRecorded.Subscribe(SiegeDamageRecordedAsync);
        _combat.DamageAdjusters.Subscribe((_, target, row, _, damage) => SiegeTagDamage(target, row, damage));
        _combat.PvpDealtAsync.Subscribe(SiegePvpRecordedAsync);
        _combat.HealedAsync.Subscribe(SiegeHealRecordedAsync);
    }

    internal void ResetTestWindow() => _siegeTestOpen = null;
    internal void ResetPublishedStateForTest() => _siegePublishedState = "";

    private GameData _data => _territory.GameData;
    private AccountStore _accounts => _territory.AccountStore;
    private RoleStore _roles => _territory.Roles;
    private GuildStore _guildStore => _territory.GuildStore;
    private GuildService _guilds => _territory.Guilds;
    private CharacterService _character => _territory.Character;
    private FunctionService _functions => _territory.Functions;
    private TerritoryWarsClock _clock => _territory.Clock;
    private SessionRegistry _sessions => _territory.Sessions;
    private InstanceService _instances => _territory.Instances;
    private SceneService _scene => _territory.Scene;
    private WorldData _maps => _territory.Maps;
    private BuffService _buffs => _territory.Buffs;
    private CombatService _combat => _territory.Combat;
    private RewardService _rewards => _territory.Rewards;
    private NewActivityService _activities => _territory.Activities;
    private GameLoop _loop => _territory.Loop;
    private SemaphoreSlim _gate => _territory.Gate;
    private WorldState state { get => _territory.CurrentState; set => _territory.CurrentState = value; }
    private Dictionary<string, SiegeRun> _siegeBattles => _territory.SiegeBattles;
    private ConcurrentDictionary<long, (VehicleUnit Unit, InstanceService.Room Room)> _vehicles => _territory.Vehicles;
    private SyncHook<long, long, bool> SiegeSettled => _territory.SiegeSettled;

    private IDisposable StageState() => _territory.StageState();
    private Task<Ownership.LegacyOwnerScope?> EnterGateAsync(string origin, GameSession? actor = null,
        IReadOnlyCollection<long>? additionalGuilds = null, bool includeParticipants = false) =>
        _territory.EnterGateAsync(origin, actor, additionalGuilds, includeParticipants);
    private IEnumerable<JsonElement> Zones => _territory.Zones;
    private int ZoneType(int id) => _territory.ZoneType(id);
    private int RootOf(int id) => _territory.RootOf(id);
    private long OwnerOf(int zone) => _territory.OwnerOf(zone);
    private long[] QualifiedFor(int territory, int? roundId = null) => _territory.QualifiedFor(territory, roundId);
    private GuildStore.State? Own(GameSession s) => _territory.Own(s);
    private long OwnId(GameSession s) => _territory.OwnId(s);
    private Task Send(GameSession s, uint id, IMessage msg) => _territory.Send(s, id, msg);
    private Task SyncSiegeAsync(GameSession s, RoleState? committedRole = null) => _territory.SyncSiegeAsync(s, committedRole);
    private Task SaveAsync(WorldState value, string kind = "state", GameSession? actor = null, string? sourceKey = null) =>
        _territory.SaveAsync(value, kind, actor, sourceKey);
    private void PublishCommitted(WorldState value) => _territory.PublishCommitted(value);
    private RoleState LoadRole(long id) => _territory.LoadRole(id);
    private Task<long> AllocateStatisticsIdAsync() => _territory.AllocateStatisticsIdAsync();
    private void AssignTerritoryOwner(WorldState next, int zone, long winner) => _territory.AssignTerritoryOwner(next, zone, winner);
    private Task FlushActivityAsync() => _territory.FlushActivityAsync();
    private Task PublishAsync() => _territory.PublishAsync();
    private Task ReconcileLockedAsync() => _territory.ReconcileLockedAsync();
    private Task SendCommandBoardAsync(GameSession s) => _territory.SendCommandBoardAsync(s);
    private bool MachineHittable(GameSession s, SiegeRun battle, long camp) => _territory.MachineHittable(s, battle, camp);
    private EventEconomyMutation? SiegeMailSource(IReadOnlyDictionary<long, List<MailService.Attachment>> awards, string sourceKey) =>
        _territory.SiegeMailSource(awards, sourceKey);
    private void SiegeTrack(SiegeRun battle, IEnumerable<long> roles) => _territory.SiegeTrack(battle, roles);
    private VehicleUnit? NewVehicle(int npcId, InstanceService.Room room, MapPoint spot, long owner, long camp, long guild) =>
        _territory.NewVehicle(npcId, room, spot, owner, camp, guild);
}
