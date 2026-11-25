using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guardian pets, tower pets, and allied companions in combat: casts, reactions, windows, auras, and tower/companion
/// attacks. Extracted from <see cref="CombatService"/> as its own DI singleton service.
/// </summary>
public sealed partial class PetCombatService
{
    private const uint UseSkill = CombatService.UseSkill;
    private const uint UseInSkill = CombatService.UseInSkill;
    private const uint SkillHit = CombatService.SkillHit;
    private const uint SkillEnd = CombatService.SkillEnd;
    private const uint SkillMove = CombatService.SkillMove;
    private const uint AoiAttrChange = CombatService.AoiAttrChange;
    private const uint EntityBaseChange = CombatService.EntityBaseChange;

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
    private readonly PetService _pets;
    private readonly ILogger<PetCombatService> _log;

    public PetCombatService(CombatService combat, World world, MonsterService monsters, CharacterService character,
        SkillService skills, SceneService scene, BuffService buffs, SessionRegistry sessions, GameData data,
        GameLoop loop, PetService pets, ILogger<PetCombatService> log)
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
        _pets = pets;
        _log = log;
        _monsters.AttackPet = MonsterAttackPetAsync;
        _buffs.UnitChanged.Subscribe(PublishTowerPetStatsAsync);
        WireGuardians();
        WireCompanions();
    }

    private long Now => _loop.NowMs;
    private long NextSessionId() => _combat.NextSessionId();
    private CombatRolls Rolls => _combat.Rolls;
    private AsyncHook<GameSession, MonsterUnit, JsonElement, long> DealtAsync => _combat.DealtAsync;
    private AsyncHook<MonsterUnit, GameSession, long> TakenAsync => _combat.TakenAsync;
    private OrderedHook<Func<GameSession, MonsterUnit, JsonElement, DamageHitResult, long, long>> DamageAdjusters => _combat.DamageAdjusters;
    private AsyncHook<GameSession, ActiveSkillCast, IReadOnlyList<MonsterUnit>, SkillHitInfo> CastResolved => _combat.CastResolved;
    private OrderedHook<Func<GameSession, long, long>> ExtraHeal => _combat.ExtraHeal;
    private SyncHook<GameSession, long> Healed => _combat.Healed;
    private AsyncHook<GameSession, long> HealedAsync => _combat.HealedAsync;
    private AsyncHook<TowerPetUnit> TowerPetDied => _combat.TowerPetDied;
    private OrderedHook<Func<MonsterUnit, long, bool, long>> MonsterDamageAdjusters => _combat.MonsterDamageAdjusters;

    private void ScheduleRuntime(long worldScene, TimeSpan delay, Func<Task> work) => _combat.ScheduleRuntime(worldScene, delay, work);
    private (long Damage, DamageHitResult Result) Damage(CombatService.Fighter attacker, CombatService.Fighter defender, JsonElement row,
        int branch = 0, double bonusCrit = 0, bool guaranteedCrit = false) =>
        _combat.Damage(attacker, defender, row, branch, bonusCrit, guaranteedCrit);
    private double ExpectedDamage(CombatService.Fighter attacker, CombatService.Fighter defender, JsonElement row) =>
        _combat.ExpectedDamage(attacker, defender, row);
    private CombatService.Fighter MonsterFighter(MonsterUnit monster) => _combat.MonsterFighter(monster);
    private Task SettleMonsterHitAsync(GameSession player, MonsterUnit monster, long previousHp, long damage) =>
        _combat.SettleMonsterHitAsync(player, monster, previousHp, damage);
    private Task PublishDurableKillAsync(GameSession player, MonsterUnit monster, Func<bool> current) =>
        _combat.PublishDurableKillAsync(player, monster, current);
    private Task FinalizeLethalPublicationAsync(MonsterUnit monster) => _combat.FinalizeLethalPublicationAsync(monster);
    private Task FightingAsync(MonsterUnit monster) => _combat.FightingAsync(monster);
    private int HitEffect(long scriptShow) => _combat.HitEffect(scriptShow);
    private int HealEffect(long scriptShow) => _combat.HealEffect(scriptShow);
    private Task RefuseCastAsync(GameSession s, CastSkillReq req, int error, long sessionId) => _combat.RefuseCastAsync(s, req, error, sessionId);
    private Task StartCooldownAsync(GameSession s, JsonElement row) => _combat.StartCooldownAsync(s, row);
    private Task SendCastTimelineAsync(GameSession s, UseInSkillInfo info, bool extra = false) => _combat.SendCastTimelineAsync(s, info, extra: extra);
    private Task ApplyEffectsAsync(GameSession s, ActiveSkillCast action, Vec3 center, SkillObject? field = null,
        IReadOnlyList<MonsterUnit>? chosenTargets = null, IReadOnlyList<GameSession>? chosenAllies = null) =>
        _combat.ApplyEffectsAsync(s, action, center, field, chosenTargets, chosenAllies);
    private static bool CurrentCastMap(GameSession s, ActiveSkillCast action) => CombatService.CurrentCastMap(s, action);
    private bool? EvaluateMatchSkillRule(GameSession s, int skillId) => _combat.EvaluateMatchSkillRule(s, skillId);
    private int ValidateTarget(GameSession s, JsonElement row, CastSkillReq req, Vec3 origin, float slack = 0, float askedRange = 0) =>
        _combat.ValidateTarget(s, row, req, origin, slack, askedRange);
    private bool IsPlayerHostile(GameSession a, GameSession b) => _combat.IsPlayerHostile(a, b);
    private bool CanPlayerHit(GameSession a, GameSession b) => _combat.CanPlayerHit(a, b);
    private Task ToPlayerAndViewersAsync(GameSession s, uint msgId, Google.Protobuf.IMessage message) =>
        _combat.ToPlayerAndViewersAsync(s, msgId, message);
    private static long ModifiedHealing(StatSheet healer, StatSheet target, long amount) =>
        CombatService.ModifiedHealing(healer, target, amount);
    private StatSheet HealerAttributes(GameSession s) => _combat.HealerAttributes(s);
    private bool Stealthed(GameSession p) => _combat.Stealthed(p);
    private static bool Airborne(GameSession p) => CombatService.Airborne(p);
    private static long ScaleMonsterDamage(long damage, int ratio) => CombatService.ScaleMonsterDamage(damage, ratio);
    private long MonsterBuffDamage(MonsterUnit monster, JsonElement? row, long damage) => _combat.MonsterBuffDamage(monster, row, damage);
    private static float Distance(Vec3 a, Vec3 b) => CombatService.Distance(a, b);
    private static float PlayerDistance(GameSession a, GameSession b) => CombatService.PlayerDistance(a, b);
    private static double[] Numbers(JsonElement row, string field) => CombatService.Numbers(row, field);
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string field) => CombatService.Tuples(row, field);
    private static string Script(JsonElement row) => CombatService.Script(row);
    private static float Range(JsonElement row) => CombatService.Range(row);
    private static bool Finite(Vec3 p) => CombatService.Finite(p);
    private static int ImpactOffset(JsonElement? show, double point, int chantMs, int actionMs) =>
        CombatService.ImpactOffset(show, point, chantMs, actionMs);
    private bool RollBuff(JsonElement tuple, out int id, out int ms) => _combat.RollBuff(tuple, out id, out ms);
    private bool RollHostileBuff(JsonElement tuple, GameSession? attackerPlayer, WorldUnit? attackerUnit,
        GameSession? defenderPlayer, MonsterUnit? defenderMonster, out int id, out int durationMs) =>
        _combat.RollHostileBuff(tuple, attackerPlayer, attackerUnit, defenderPlayer, defenderMonster, out id, out durationMs);
    private static IEnumerable<JsonElement> HostileBuffs(JsonElement row) => CombatService.HostileBuffs(row);
    private GameSession[] EventAreaTargets(GameSession s, JsonElement row) => _combat.EventAreaTargets(s, row);
    private MonsterUnit? FindMonster(long sceneId, long uid) => _combat.FindMonster(sceneId, uid);
    private List<MonsterUnit> SelectEnemies(GameSession s, ActiveSkillCast action, Vec3 center, float? radiusOverride = null) =>
        _combat.SelectEnemies(s, action, center, radiusOverride);
    private GameSession? EventTarget(GameSession source, long id) => _combat.EventTarget(source, id);
    private Vec3 WalkableEnd(long sceneId, Vec3 start, float dx, float dz) => _combat.WalkableEnd(sceneId, start, dx, dz);
    private (int Id, int Ms) MovementEffect(long scriptShow) => _combat.MovementEffect(scriptShow);
    private static (double Forward, double Side) FacingOffset(double dx, double dz, float face) =>
        CombatService.FacingOffset(dx, dz, face);
}
