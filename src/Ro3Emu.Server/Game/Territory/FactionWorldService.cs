using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.TerritoryWarsService;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Public territory PvP world (pickups, drops, activities), Battle Merit (Glory) and season payouts, territory auctions,
/// progression rules, stronghold gameplay scene points, quest targets, and panel snapshot. Extracted from
/// <see cref="TerritoryWarsService"/>.
/// </summary>
public sealed partial class FactionWorldService : IDisposable
{
    public void Dispose() => _worldGate.Dispose();
    private readonly TerritoryWarsService _territory;
    private readonly AwardService _awards;
    private readonly MailService _mail;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly ILogger<FactionWorldService> _log;

    public FactionWorldService(TerritoryWarsService territory, AwardService awards, MailService mail, World world,
        MonsterService monsters, ILogger<FactionWorldService> log)
    {
        _territory = territory;
        _awards = awards;
        _mail = mail;
        _world = world;
        _monsters = monsters;
        _log = log;
        _scene.Loaded.Subscribe(WorldLoadedAsync);
        _scene.Leaving.Subscribe(GloryLeavingAsync);
        _combat.Killed.Subscribe(WorldKilledAsync);
        _combat.PlayerDied.Subscribe(WorldDiedAsync);
        _combat.PlayerKilled.Subscribe(GloryKilledAsync);
        _combat.PvpDealtAsync.Subscribe(GloryHitAsync);
        _character.StateChanged.Subscribe(s => _ = SyncGloryAsync(s));
    }

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
    private CombatService _combat => _territory.Combat;
    private GameLoop _loop => _territory.Loop;
    private SemaphoreSlim _gate => _territory.Gate;
    private WorldState state => _territory.CurrentState;
    private Dictionary<int, Battle> _battles => _territory.Battles;
    private string Epoch => _territory.Epoch;

    private SyncHook<GameSession> CrystalCollected => _territory.CrystalCollected;
    private SyncHook<GameSession, long, long, int> SiegeTracked => _territory.SiegeTracked;
    private Func<long, Task<int>>? GloryBoardRank => _territory.GloryBoardRank;
    private Func<long, int, long> GuildEscortScore => _territory.GuildEscortScore;
    private Action? BeforeWorldPickupApplyForTest => _territory.BeforeWorldPickupApplyForTest;
    private Action? BeforeWorldDeathApplyForTest => _territory.BeforeWorldDeathApplyForTest;

    private IDisposable StageState() => _territory.StageState();
    private Task<Ownership.LegacyOwnerScope?> EnterGateAsync(string origin, GameSession? actor = null,
        IReadOnlyCollection<long>? additionalGuilds = null, bool includeParticipants = false) =>
        _territory.EnterGateAsync(origin, actor, additionalGuilds, includeParticipants);
    private Ownership.ReservationKey[] MutationKeys(GameSession? actor, IReadOnlyCollection<long>? additionalGuilds = null,
        bool includeParticipants = false) => _territory.MutationKeys(actor, additionalGuilds, includeParticipants);
    private IEnumerable<JsonElement> Zones => _territory.Zones;
    private int Parent(int id) => _territory.Parent(id);
    private int ZoneType(int id) => _territory.ZoneType(id);
    private int RootOf(int id) => _territory.RootOf(id);
    private long OwnerOf(int id) => _territory.OwnerOf(id);
    private GuildStore.State? Own(GameSession s) => _territory.Own(s);
    private long OwnId(GameSession s) => _territory.OwnId(s);
    private Task Send(GameSession s, uint id, IMessage msg) => _territory.Send(s, id, msg);
    private Task Error(GameSession s, uint request, int error) => _territory.Error(s, request, error);
    private Task SaveAsync(WorldState value, string kind = "state", GameSession? actor = null, string? sourceKey = null) =>
        _territory.SaveAsync(value, kind, actor, sourceKey);
    private Task CommitAsync(WorldState next, IEnumerable<GuildStore.State>? changed = null,
        string kind = "transition", GameSession? actor = null, string? sourceKey = null) =>
        _territory.CommitAsync(next, changed, kind, actor, sourceKey);
    private void PublishCommitted(WorldState value) => _territory.PublishCommitted(value);
    private RoleState LoadRole(long id) => _territory.LoadRole(id);
    private Task PublishAsync() => _territory.PublishAsync();
    private void QueueActivity(GameSession s) => _territory.QueueActivity(s);
    private void AddRankAward(List<Grant> grants, string table, int rank, GameSession s) =>
        _territory.AddRankAward(grants, table, rank, s);
    private void AddAward(List<Grant> grants, long group, GameSession s) => _territory.AddAward(grants, group, s);
    private Task DeliverAsync(GameSession s) => _territory.DeliverAsync(s);
    private Economy.EventEconomyMutation? PendingSource(IReadOnlyDictionary<long, List<Grant>> awards, string kind, string sourceKey) =>
        TerritoryWarsEconomy.PendingSource(_data, _loop.NowMs, awards, kind, sourceKey);
    private TerritoryWarsClock.Stage? WarStage(int round) => _territory.WarStage(round);
    private (long Attack, long Defense) SiegeGuilds(int city) => _territory.Siege.SiegeGuilds(city);
    private TimedTodoSchedule.Occurrence? SiegeWindow() => _territory.Siege.SiegeWindow();
    private int SiegeZone(int city) => _territory.Siege.SiegeZone(city);
    private static string SiegeKey(int city, long start) => SiegeService.SiegeKey(city, start);
}
