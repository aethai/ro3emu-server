using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Attempt = Ro3Emu.Server.Game.DungeonService.Attempt;
using State = Ro3Emu.Server.Game.DungeonService.State;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Reconstructed scripted scenes, rides, slides, hide-and-seek, Hidden Realm rolling, Journey dungeons, Dracula's Lair,
/// Guild Challenge rooms, guide ways, region entry, continuations, and plane exits.
/// Extracted from <see cref="DungeonService"/>.
/// </summary>
public sealed partial class ScriptedDungeonService(DungeonService dungeons, WorldInteractionService vehicles,
    SceneService scenes, World world, BuffService buffs, MonsterService monsters, CombatService combat)
{
    private const uint BaseInfo = DungeonService.BaseInfo;
    private const uint Event = DungeonService.Event;
    private const uint Enter = DungeonService.Enter;
    private const uint Leave = DungeonService.Leave;
    private const uint CreateResult = DungeonService.CreateResult;
    private const uint EnterSceneResult = DungeonService.EnterSceneResult;

    private readonly DungeonService _dungeons = dungeons;
    private readonly WorldInteractionService _vehicles = vehicles;
    private readonly SceneService _scenes = scenes;
    private readonly World _world = world;
    private readonly BuffService _buffs = buffs;
    private readonly MonsterService _monsters = monsters;
    private readonly CombatService _combat = combat;
    private readonly ILogger _log = dungeons.Log;

    internal void WireScene()
    {
        // A rider's client reports the carrier's walk; a sliding client moves faster than it walks (ScriptedDungeonService.Slide).
        _scenes.WalkOnRide = s => _vehicles.IsSeatedOnSceneActor(s) || Sliding(s);
        _vehicles.ExitHeld = RideHolds;
        _scenes.Loaded.Subscribe(_dungeons.LoadedAsync);
        _scenes.Leaving.Subscribe(_dungeons.AbandonAsync);
        _scenes.Leaving.Subscribe(StopSlideAsync);
    }

    internal void EngageMonster(MonsterUnit monster, GameSession target) => _monsters.Engage(monster, target);

    private GameData _data => _dungeons.GameData;
    private DungeonCatalog _catalog => _dungeons.Catalog;
    private Ownership.OwnerRegistry _owners => _dungeons.Owners;
    private FunctionService _functions => _dungeons.Functions;
    private SeasonService _seasons => _dungeons.Seasons;
    private InstanceService _instances => _dungeons.Instances;
    private TeamService _teams => _dungeons.Teams;
    private SessionRegistry _sessions => _dungeons.Sessions;
    private GameLoop _loop => _dungeons.Loop;
    private TimeProvider _clock => _dungeons.Clock;
    private ServerOptions _options => _dungeons.Options;
    private RaidTrialsService Raids => _dungeons.Raids;

    private static Attempt? Active(GameSession s) => DungeonService.Active(s);
    private static GameSession[] Present(Attempt a) => DungeonService.Present(a);
    private static Dictionary<int, long> Inventory(GameSession s) => DungeonService.Inventory(s);
    private static DungeonVar? GoalParams(Attempt a) => DungeonService.GoalParams(a);
    private Task SendQuietAsync(GameSession s, uint id, IMessage msg) => _dungeons.SendQuietAsync(s, id, msg);
    private Task BroadcastAsync(Attempt a, uint id, IMessage msg) => _dungeons.BroadcastAsync(a, id, msg);
    private int Slot(Attempt a, long role) => _dungeons.Slot(a, role);
    private Task StartAsync(Attempt a) => _dungeons.StartAsync(a);
    private Task BeginFightAsync(Attempt a) => _dungeons.BeginFightAsync(a);
    private Task SpawnWaveAsync(Attempt a) => _dungeons.SpawnWaveAsync(a);
    private Task AdvanceAsync(Attempt a) => _dungeons.AdvanceAsync(a);
    private Task SettleAsync(Attempt a, bool won) => _dungeons.SettleAsync(a, won);
    private static State Copy(State state) => DungeonService.Copy(state);
    private Task SaveAsync(GameSession s, State state) => _dungeons.SaveAsync(s, state);
    private State CurrentState(State previous) => _dungeons.CurrentState(previous);
    private bool Meets(GameSession s, JsonElement row) => _dungeons.Meets(s, row);
    private DungeonBaseInfoNotify Snapshot(Attempt a) => _dungeons.Snapshot(a);
    private int Admission(GameSession s, DungeonCatalog.Plan plan, bool continuation = false) =>
        _dungeons.Admission(s, plan, continuation);
    private Task BeginAsync(DungeonCatalog.Plan plan, GameSession[] members, long leader, long team, bool matched = false) =>
        _dungeons.BeginAsync(plan, members, leader, team, matched);
    private Task EnterAsync(GameSession s, EnterDungeonReq req, DungeonService.EntryParty party = DungeonService.EntryParty.Team) =>
        _dungeons.EnterAsync(s, req, party);
    private Task LeaveAsync(GameSession s) => _dungeons.LeaveAsync(s);
    private TimeSpan Paced(int ms) => Raids.Paced(ms);
    private Task BoardCannonAsync(Attempt a, GameSession s, SceneActorUnit actor, long interaction) =>
        Raids.BoardCannonAsync(a, s, actor, interaction);
    private Task FlyAsync(Attempt a, MonsterUnit unit, IReadOnlyList<(float X, float Y, float Z)> route) =>
        Raids.FlyAsync(a, unit, route);
    private Task SyncCompanionsAsync(Attempt a, IReadOnlyList<ReconstructedDungeons.SceneActor> wanted) =>
        Raids.SyncCompanionsAsync(a, wanted);
    private void StartReaction(Attempt a, ReconstructedDungeons.Reaction react) => Raids.StartReaction(a, react);
    private Task<int> RespondAsync(Attempt a, ReconstructedDungeons.Reaction? react,
        ReconstructedDungeons.Response? response, SceneActorUnit? used) =>
        Raids.RespondAsync(a, react, response, used);
    private void StartShelter(Attempt a, ReconstructedDungeons.Shelter shelter) => Raids.StartShelter(a, shelter);
    private Task<bool> TryTrialInteractAsync(GameSession s, NpcInteractionReq req) => Raids.TryTrialInteractAsync(s, req);
    private Task TrialTouchesAsync(Attempt a, TrialRun run) => Raids.TrialTouchesAsync(a, run);
}
