using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Match = Ro3Emu.Server.Game.TimedBattleService.Match;
using State = Ro3Emu.Server.Game.TimedBattleService.State;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle ("Chicken", match entry 2, scene 2240101): stage machine, careers, landing, items/chests,
/// poison zone, death/revival, spectating, appearance/emoji, and follow. Extracted from <see cref="TimedBattleService"/>.
/// </summary>
public sealed partial class ChickenBattleService
{
    private const int ChickenEntry = TimedBattleService.ChickenEntry;
    private const uint ChickenResult = TimedBattleService.ChickenResult;

    private readonly TimedBattleService _timed;
    private readonly SkillService _skills;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly TestingSwitches _testing;

    public ChickenBattleService(TimedBattleService timed, SkillService skills, World world, MonsterService monsters,
        TestingSwitches testing)
    {
        _timed = timed;
        _skills = skills;
        _world = world;
        _monsters = monsters;
        _testing = testing;
        _combat.PlayerDied.Subscribe(ChickenDiedAsync);
        _combat.ReviveInfoOwners.Subscribe(OwnsRevive);
        _combat.PvpDealt.Subscribe(ChickenDealt);
        _combat.Healed.Subscribe(ChickenHealed);
        _combat.CastLanded.Subscribe(ChickenCastAsync);
        _world.CanObserve = CanBeSeen;
        _character.MatchAttributes = MatchAttributes;
    }

    private GameData _data => _timed.GameData;
    private TimeProvider _time => _timed.Time;
    private CharacterService _character => _timed.Character;
    private TaskService _tasks => _timed.Tasks;
    private InstanceService _instances => _timed.Instances;
    private SceneService _scene => _timed.Scene;
    private WorldData _worldData => _timed.WorldData;
    private CombatService _combat => _timed.Combat;
    private BuffService _buffs => _timed.Buffs;
    private GameLoop _loop => _timed.Loop;
    private ILogger _log => _timed.Log;
    private Dictionary<GameSession, Match> _active => _timed.ActiveMatches;
    private ChickenEncounterService Encounters => _timed.ChickenEncounters;

    private Task RunMatchTransitionAsync(Match match, string origin, Func<Task> work) =>
        _timed.RunMatchTransitionAsync(match, origin, work);
    private Task SettleMatchAsync(Match match) => _timed.SettleMatchAsync(match);
    private Task AbortMatchAsync(Match match, GameSession? departed) => _timed.AbortMatchAsync(match, departed);
    private static HashSet<int> RemainingFactions(Match match) => TimedBattleService.RemainingFactions(match);
    private Task SaveAsync(GameSession session, State next) => _timed.SaveAsync(session, next);
    private Task SyncBattleStateAsync(GameSession session, bool login = false) => _timed.SyncBattleStateAsync(session, login);
    private long ChickenRankFloor(int rank) => _timed.ChickenRankFloor(rank);
    private ChickenResultNotify ChickenSnapshot(Match match, GameSession recipient, bool actualVictory) =>
        _timed.ChickenSnapshot(match, recipient, actualVictory);
    private static (long Id, string Name, int Career) Presentation(Match match, GameSession player) =>
        TimedBattleService.Presentation(match, player);
    private Task SpawnShopsAsync(Match match, ChickenRun run) => Encounters.SpawnShopsAsync(match, run);
    private Task SpawnDiceAsync(Match match, ChickenRun run) => Encounters.SpawnDiceAsync(match, run);
    private Task TickAdventureAsync(Match match, ChickenRun run, long now) => Encounters.TickAdventureAsync(match, run, now);
    private Task SyncAdventureAsync(Match match, GameSession session, ChickenPlayer player) => Encounters.SyncAdventureAsync(match, session, player);
    private static Task ClearAdventureAsync(GameSession session) => ChickenEncounterService.ClearAdventureAsync(session);
}
