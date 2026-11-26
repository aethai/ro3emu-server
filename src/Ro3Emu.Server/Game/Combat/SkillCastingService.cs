using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skill casting pipeline, 31124 interrupt, cooldowns, channels, skill protocol, reading, class loops, staged skills,
/// and skill conditions. Extracted from <see cref="CombatService"/> as its own DI singleton service.
/// </summary>
public sealed partial class SkillCastingService
{
    private const uint UseSkill = CombatService.UseSkill;
    private const uint UseInSkill = CombatService.UseInSkill;
    private const uint SkillHit = CombatService.SkillHit;
    private const uint SkillEnd = CombatService.SkillEnd;
    private const uint SkillInterrupt = CombatService.SkillInterrupt;
    private const uint SkillCdUpdate = CombatService.SkillCdUpdate;

    private readonly CombatService _combat;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly CharacterService _character;
    private readonly SkillService _skills;
    private readonly SceneService _scene;
    private readonly BuffService _buffs;
    private readonly SessionRegistry _sessions;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly ILogger<SkillCastingService> _log;

    public SkillCastingService(CombatService combat, World world, MonsterService monsters, CharacterService character,
        SkillService skills, SceneService scene, BuffService buffs, SessionRegistry sessions, GameData data,
        GameLoop loop, SeasonTalentService talents, ILogger<SkillCastingService> log)
    {
        _combat = combat;
        _world = world;
        _monsters = monsters;
        _character = character;
        _skills = skills;
        _scene = scene;
        _buffs = buffs;
        _sessions = sessions;
        _data = data;
        _loop = loop;
        _log = log;
        skills.Learned.Subscribe(RefreshSkillParametersAsync, order: -10);
        buffs.Changed.Subscribe(RefreshSkillParametersAsync, order: -10);
        talents.EffectsChanged.Subscribe(RefreshSkillParametersAsync);
        scene.Loaded.Subscribe(RefreshSkillParametersAsync);
        scene.Leaving.Subscribe(ClearSceneSkillProtocolAsync, order: SceneService.LeaveOrderCombat);
        skills.QueueEligibility = CanQueueSkill;
        CastResolved.Subscribe(ReadingExtraCastAsync);
        CastResolved.Subscribe(ClassLoopsOnCastAsync);
    }

    private long Now => _loop.NowMs;
    private long NextSessionId() => _combat.NextSessionId();
    private CombatRolls Rolls => _combat.Rolls;
    private PetCombatService PetCombat => _combat.PetCombat;
    private CombatEffectsService Effects => _combat.Effects;

    private OrderedHook<Func<GameSession, int, Task<int>>> BeforeCast => _combat.BeforeCast;
    private OrderedHook<Func<GameSession, int, bool>> GrantedSkill => _combat.GrantedSkill;
    private Func<GameSession, int, bool>? AimsFromSeat => _combat.AimsFromSeat;
    private Func<GameSession, GameSession, int, bool>? SkillBuffAllowed => _combat.SkillBuffAllowed;
    private Func<GameSession, GameSession, JsonElement, Task>? ReviveOffer => _combat.ReviveOffer;
    private Func<GameSession, int>? ReviveCheck => _combat.ReviveCheck;
    private Func<GameSession, GameSession, JsonElement, Task<bool>>? ReviveItemSkill => _combat.ReviveItemSkill;
    private AsyncHook<GameSession, ActiveSkillCast, IReadOnlyList<MonsterUnit>, SkillHitInfo> CastResolved => _combat.CastResolved;

    private void ScheduleRuntime(long worldScene, TimeSpan delay, Func<Task> work) => _combat.ScheduleRuntime(worldScene, delay, work);
    private static bool CurrentCastMap(GameSession s, ActiveSkillCast action) => CombatService.CurrentCastMap(s, action);
    private int ChantMs(GameSession s, JsonElement row) => _combat.ChantMs(s, row);
    private int HitEffect(long scriptShow) => _combat.HitEffect(scriptShow);
    private Task ToPlayerAndViewersAsync(GameSession s, uint msgId, IMessage message) => _combat.ToPlayerAndViewersAsync(s, msgId, message);
    private Task FightingAsync(GameSession s) => _combat.FightingAsync(s);
    private bool CanHitMonster(GameSession source, MonsterUnit target) => _combat.CanHitMonster(source, target);
    private bool CanPlayerHit(GameSession source, GameSession target) => _combat.CanPlayerHit(source, target);
    private bool IsPlayerHostile(GameSession a, GameSession b) => _combat.IsPlayerHostile(a, b);
    private GameSession? EventTarget(GameSession source, long id) => _combat.EventTarget(source, id);
    private static float PlayerDistance(GameSession a, GameSession b) => CombatService.PlayerDistance(a, b);
    private (ICombatRule Rule, int Result)? TryAdmitOwnSkill(GameSession session, int skillId) => _combat.TryAdmitOwnSkill(session, skillId);
    private bool? EvaluateMatchSkillRule(GameSession session, int skillId) => _combat.EvaluateMatchSkillRule(session, skillId);
    private bool IsSkillLineBlocked(GameSession session, MonsterUnit? target, Vec3 origin, Vec3 destination) =>
        _combat.IsSkillLineBlocked(session, target, origin, destination);
    private static long ModifiedSpCost(StatSheet stats, long amount) => CombatService.ModifiedSpCost(stats, amount);
    private static bool ControlCannotInterrupt(GameSession s) => CombatService.ControlCannotInterrupt(s);
    private List<MonsterUnit> MonstersNear(GameSession s, Vec3 center, float radius) => Effects.MonstersNear(s, center, radius);
    private IReadOnlyList<(long[] Weapons, SkillParameterRules.Modifier Modifier)> PassiveModifiers(GameSession session) =>
        _combat.PassiveModifiers(session);
    private bool CanSee(GameSession viewer, GameSession subject) => Effects.CanSee(viewer, subject);
    private Task ResolveCastAsync(GameSession s, ActiveSkillCast action) => Effects.ResolveCastAsync(s, action);
    private Task ApplyEffectsAsync(GameSession s, ActiveSkillCast action, Vec3 center, SkillObject? field = null,
        IReadOnlyList<MonsterUnit>? chosenTargets = null, IReadOnlyList<GameSession>? chosenAllies = null) =>
        Effects.ApplyEffectsAsync(s, action, center, field, chosenTargets, chosenAllies);
    private Task CastExtraAsync(GameSession s, JsonElement row, IReadOnlyList<MonsterUnit> targets, bool parametersApplied = false,
        long sessionId = 0) =>
        Effects.CastExtraAsync(s, row, targets, parametersApplied, sessionId);
    private Task CastExtraOnAlliesAsync(GameSession s, JsonElement row, IReadOnlyList<GameSession> allies) =>
        Effects.CastExtraOnAlliesAsync(s, row, allies);
    private JsonElement ExtraRow(GameSession s, JsonElement row) => Effects.ExtraRow(s, row);
    private List<MonsterUnit> MonstersInCastArea(GameSession s, ActiveSkillCast action) => Effects.MonstersInCastArea(s, action);
    private (int Id, int Ms) MovementEffect(long scriptShow) => Effects.MovementEffect(scriptShow);
    private (CombatEffectsService.DisplaceKind Kind, float Distance, int EffectId, int Ms)? AlsoMovesCaster(JsonElement row) =>
        Effects.AlsoMovesCaster(row);
    private Task MoveCasterAsync(GameSession s, ActiveSkillCast action, (CombatEffectsService.DisplaceKind Kind, float Distance, int EffectId, int Ms) move) =>
        Effects.MoveCasterAsync(s, action, move);
    private Task ApplySkillBuffAsync(StatSheet casterStats, GameSession recipient, int buffId, int durationMs, long sourceId) =>
        _combat.ApplySkillBuffAsync(casterStats, recipient, buffId, durationMs, sourceId);
    private SkillConfig? LearnedPassive(GameSession session, int group) => _combat.LearnedPassive(session, group);
    private Dictionary<GameSession, CombatService.EventAttack> EventAttacks => _combat.EventAttacks;
    private Task SwingEventAsync(GameSession source, GameSession target, int skillId, long castId) =>
        _combat.SwingEventAsync(source, target, skillId, castId);
}
