using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Attempt = Ro3Emu.Server.Game.DungeonService.Attempt;
using Record = Ro3Emu.Server.Game.DungeonService.Record;
using State = Ro3Emu.Server.Game.DungeonService.State;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Raid trials, Realm of the Gods (hall of fame, Sacred Blessings, Final Trial honors), first-pass rewards,
/// party bonds, dungeon statistics, reaction/shelter mechanics, cannons, and allied companions.
/// Extracted from <see cref="DungeonService"/>.
/// </summary>
public sealed partial class RaidTrialsService(DungeonService dungeons, MonsterService monsters, CombatService combat,
    BuffService buffs, World world, AccountStore accounts, AwardService awards, WorldInteractionService vehicles,
    SceneService scenes)
{
    private const uint Sync = DungeonService.Sync;
    private const uint ClaimFirstPass = DungeonService.ClaimFirstPass;
    private const uint Event = DungeonService.Event;
    private const uint BaseInfo = DungeonService.BaseInfo;
    private const uint SetJobPosition = DungeonService.SetJobPosition;
    private const uint SystemBroadcast = ScriptedDungeonService.SystemBroadcast;
    private const float SceneActorReach = ScriptedDungeonService.SceneActorReach;

    private GameData _data => dungeons.GameData;
    private DungeonCatalog _catalog => dungeons.Catalog;
    private CharacterService _character => dungeons.Character;
    private SeasonService _seasons => dungeons.Seasons;
    private InstanceService _instances => dungeons.Instances;
    private TeamService _teams => dungeons.Teams;
    private SessionRegistry _sessions => dungeons.Sessions;
    private GuildStore _guilds => dungeons.Guilds;
    private MonsterService _monsters => monsters;
    private CombatService _combat => combat;
    private BuffService _buffs => buffs;
    private SceneService _scenes => scenes;
    private World _world => world;
    private GameLoop _loop => dungeons.Loop;
    private TimeProvider _clock => dungeons.Clock;
    private ILogger _log => dungeons.Log;
    private AccountStore _accounts => accounts;
    private AwardService _awards => awards;
    private ServerOptions _options => dungeons.Options;
    private WorldInteractionService _vehicles => vehicles;

    internal void WireCombat()
    {
        _combat.GrantedSkill.Subscribe(FiresCannon);
        _combat.AimsFromSeat = FiresCannon;
        _combat.DamageAdjusters.Subscribe(TrialDamage);
        _combat.Killed.Subscribe(TrialObjectKilledAsync);
        _combat.DealtAsync.Subscribe(TrialBossHitAsync);
        _combat.BossAreaLanded.Subscribe(TrialBreakersAsync);
        _teams.MultiDungeonInfo = TeamDungeonInfo;
        _combat.DamageAdjusters.Subscribe((s, m, _, _, damage) => BondDamage(s, m, damage, incoming: false));
        _combat.IncomingDamageAdjusters.Subscribe((m, s, _, damage) => Task.FromResult(BondDamage(s, m, damage, incoming: true)));
    }

    private static Attempt? Active(GameSession s) => DungeonService.Active(s);
    private static GameSession[] Present(Attempt a) => DungeonService.Present(a);
    private DungeonBaseInfoNotify Snapshot(Attempt a) => dungeons.Snapshot(a);
    private Task SendQuietAsync(GameSession s, uint id, IMessage msg) => dungeons.SendQuietAsync(s, id, msg);
    private Task BroadcastAsync(Attempt a, uint id, IMessage msg) => dungeons.BroadcastAsync(a, id, msg);
    private int Slot(Attempt a, long role) => dungeons.Slot(a, role);
    private Task BeginFightAsync(Attempt a) => dungeons.BeginFightAsync(a);
    private Task SpawnWaveAsync(Attempt a) => dungeons.SpawnWaveAsync(a);
    private Task AdvanceAsync(Attempt a) => dungeons.AdvanceAsync(a);
    private Task SettleAsync(Attempt a, bool won) => dungeons.SettleAsync(a, won);
    private static State Copy(State state) => DungeonService.Copy(state);
    private static string RecordKey(DungeonCatalog.Plan plan) => DungeonService.RecordKey(plan);
    private Task SaveAsync(GameSession s, State state) => dungeons.SaveAsync(s, state);
    private State CurrentState(State previous) => dungeons.CurrentState(previous);
    private bool Meets(GameSession s, JsonElement row) => dungeons.Meets(s, row);
    private List<(int Entry, int Count, int Bind)>? Award(long group, GameSession s) => dungeons.Award(group, s);
    private Task SyncAsync(GameSession s) => dungeons.SyncAsync(s);
    private long NextSceneActorUid() => dungeons.Scripted.NextSceneActorUid();
    private BuffInfo ActorBuff(SceneActorUnit unit, int configId) => dungeons.Scripted.ActorBuff(unit, configId);
    private Task SetActorBuffsAsync(SceneActorUnit unit, IReadOnlyList<int> wanted) => dungeons.Scripted.SetActorBuffsAsync(unit, wanted);
    private static ReconstructedDungeons.ScriptedStep? Step(Attempt a) => ScriptedDungeonService.Step(a);
    private void StepLater(Attempt a, int generation, int ms, Func<Task> work, bool whileFighting = false) =>
        dungeons.Scripted.StepLater(a, generation, ms, work, whileFighting);
}
