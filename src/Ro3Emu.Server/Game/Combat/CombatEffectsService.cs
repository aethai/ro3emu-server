using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skill effect resolution, ground skill objects, displacements, boss skills, buff rules, support skills, skill areas,
/// monster skills, unit strikes, and stealth rules. Extracted from <see cref="CombatService"/> as its own DI singleton.
/// </summary>
public sealed partial class CombatEffectsService
{
    private const uint UseSkill = CombatService.UseSkill;
    private const uint UseInSkill = CombatService.UseInSkill;
    private const uint SkillHit = CombatService.SkillHit;
    private const uint SkillEnd = CombatService.SkillEnd;
    private const uint SkillMove = CombatService.SkillMove;
    private const uint SkillEffect = CombatService.SkillEffect;
    private const uint TrapDamage = CombatService.TrapDamage;
    private const uint AoiAttrChange = CombatService.AoiAttrChange;
    private const uint EntityBaseChange = CombatService.EntityBaseChange;
    private const int DisplayGfx = CombatService.DisplayGfx;
    private const int DefaultHitEffect = CombatService.DefaultHitEffect;
    private const int DemonRace = CombatService.DemonRace;
    private const int UndeadElement = CombatService.UndeadElement;
    private const int SanctuaryNpc = CombatService.SanctuaryNpc;
    private const int GravitationFieldNpc = CombatService.GravitationFieldNpc;
    private const int PlayerRace = CombatService.PlayerRace;
    private const int PlayerSize = CombatService.PlayerSize;

    private readonly CombatService _combat;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly CharacterService _character;
    private readonly SkillService _skills;
    private readonly SceneService _scene;
    private readonly BuffService _buffs;
    private readonly WorldData _worldData;
    private readonly SessionRegistry _sessions;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly ILogger<CombatEffectsService> _log;

    public CombatEffectsService(CombatService combat, World world, MonsterService monsters, CharacterService character,
        SkillService skills, SceneService scene, BuffService buffs, WorldData worldData, SessionRegistry sessions,
        GameData data, GameLoop loop, ILogger<CombatEffectsService> log)
    {
        _combat = combat;
        _world = world;
        _monsters = monsters;
        _character = character;
        _skills = skills;
        _scene = scene;
        _buffs = buffs;
        _worldData = worldData;
        _sessions = sessions;
        _data = data;
        _loop = loop;
        _log = log;
        CastResolved.Subscribe(SupportSkillOnCastAsync, order: CombatService.GuardianHookOrder);
        monsters.TargetChanged.Subscribe(MoveTopHatredBuffAsync);
        WireBuffRules();
        WireBossSkills();
    }

    /// <summary>
    /// NPCConfig _kTopHatredBuff: the player a boss holds as its hatred target carries its buff. The client's boss HP widget
    /// knows one hatred target per boss (custom data BossHatredTarget_Long 74, UI_Sys_Dungeon_TopInfo_HPWidgetCtrl); that
    /// it is the boss's current target is (inferred), so the buff follows the target change, losing the target included.
    /// It is taken off by its source, the monster, so two bosses with the same buff leave each other's alone.
    /// </summary>
    private async Task MoveTopHatredBuffAsync(MonsterUnit monster, GameSession? before, GameSession? after)
    {
        int buffId = monster.Template.TopHatredBuffId;
        if (buffId <= 0) return;
        // Handlers run detached: act on the target the monster holds now, not on a change another one already replaced.
        if (before?.Role != null && !ReferenceEquals(monster.Target, before)
            && before.Buffs.FirstOrDefault(b => b.ConfigId == buffId && b.SourceId == monster.Uid) is { } held)
            await _buffs.RemoveAsync(before, held);
        if (after?.Role != null && ReferenceEquals(monster.Target, after) && !monster.Dead && after.WorldScene == monster.WorldScene
            && !after.Buffs.Any(b => b.ConfigId == buffId && b.SourceId == monster.Uid))
            await _buffs.AddUntilSpentAsync(after, buffId, monster.Uid);
    }

    private long Now => _loop.NowMs;
    private long NextSessionId() => _combat.NextSessionId();
    private CombatRolls Rolls => _combat.Rolls;
    private PetCombatService PetCombat => _combat.PetCombat;

    private AsyncHook<GameSession, JsonElement> CastLanded => _combat.CastLanded;
    private AsyncHook<GameSession, ActiveSkillCast, IReadOnlyList<MonsterUnit>, SkillHitInfo> CastResolved => _combat.CastResolved;
    private Func<GameSession, JsonElement, int, JsonElement>? TargetCountRow => _combat.TargetCountRow;
    private Func<GameSession, GameSession, int, bool>? SkillBuffAllowed => _combat.SkillBuffAllowed;
    private OrderedHook<Func<GameSession, MonsterUnit, JsonElement, DamageHitResult, long, long>> DamageAdjusters => _combat.DamageAdjusters;
    private OrderedHook<Func<GameSession, MonsterUnit, JsonElement, long, long>> ExtraHitDamage => _combat.ExtraHitDamage;
    private AsyncHook<GameSession, MonsterUnit, JsonElement, long> DealtAsync => _combat.DealtAsync;
    private AsyncHook<GameSession, MonsterUnit, long> MonsterDamageApplied => _combat.MonsterDamageApplied;
    private OrderedHook<Func<GameSession, long, long>> ExtraHeal => _combat.ExtraHeal;
    private SyncHook<GameSession, long> Healed => _combat.Healed;
    private AsyncHook<GameSession, long> HealedAsync => _combat.HealedAsync;
    private AsyncHook<MonsterUnit, BossSkill, Vec3, Vec3, float> BossAreaLanded => _combat.BossAreaLanded;
    private Func<GameSession, int, Vec3?, Task>? MountFlightSwitch => _combat.MountFlightSwitch;
    private Func<GameSession, GameSession, JsonElement, Task>? ReviveOffer => _combat.ReviveOffer;
    private Func<GameSession, GameSession, JsonElement, Task<bool>>? ReviveItemSkill => _combat.ReviveItemSkill;
    private OrderedHook<Func<MonsterUnit, long, bool, long>> MonsterDamageAdjusters => _combat.MonsterDamageAdjusters;
    private OrderedHook<Func<MonsterUnit, GameSession, DamageHitResult, long, Task<long>>> IncomingDamageAdjusters => _combat.IncomingDamageAdjusters;
    private SyncHook<MonsterUnit, GameSession, long, bool> Taken => _combat.Taken;
    private AsyncHook<MonsterUnit, GameSession, long> TakenAsync => _combat.TakenAsync;

    private void ScheduleRuntime(long worldScene, TimeSpan delay, Func<Task> work) => _combat.ScheduleRuntime(worldScene, delay, work);
    private static bool CurrentCastMap(GameSession s, ActiveSkillCast action) => CombatService.CurrentCastMap(s, action);
    private static bool Live(GameSession s, ActiveSkillCast action) => CombatService.Live(s, action);
    private (long Damage, DamageHitResult Result) Damage(CombatService.Fighter attacker, CombatService.Fighter defender, JsonElement row,
        int branch = 0, double bonusCrit = 0, bool guaranteedCrit = false) =>
        _combat.Damage(attacker, defender, row, branch, bonusCrit, guaranteedCrit);
    private double BaseDamage(CombatService.Fighter a, CombatService.Fighter b, JsonElement skill, int branch, out bool magic, bool attack = true) =>
        _combat.BaseDamage(a, b, skill, branch, out magic, attack);
    private CombatService.Fighter MonsterFighter(MonsterUnit monster) => _combat.MonsterFighter(monster);
    private StatSheet MonsterStats(MonsterUnit monster) => _combat.MonsterStats(monster);
    private Func<int, long> PlayerAttributes(GameSession p, StatSheet stats) => _combat.PlayerAttributes(p, stats);
    private int AttackElement(GameSession source, JsonElement row) => _combat.AttackElement(source, row);
    private Task SettleMonsterHitAsync(GameSession player, MonsterUnit monster, long previousHp, long damage) =>
        _combat.SettleMonsterHitAsync(player, monster, previousHp, damage);
    private Task PublishDurableKillAsync(GameSession player, MonsterUnit monster, Func<bool> current) =>
        _combat.PublishDurableKillAsync(player, monster, current);
    private Task FinalizeLethalPublicationAsync(MonsterUnit monster) => _combat.FinalizeLethalPublicationAsync(monster);
    private Task FightingAsync(MonsterUnit monster) => _combat.FightingAsync(monster);
    private Task FightingAsync(GameSession player) => _combat.FightingAsync(player);
    private int HitEffect(long scriptShow) => _combat.HitEffect(scriptShow);
    private int HealEffect(long scriptShow) => _combat.HealEffect(scriptShow);
    private int BuffHitEffect(long scriptShow) => _combat.BuffHitEffect(scriptShow);
    private static int HitSplit(JsonElement row) => CombatService.HitSplit(row);
    private int HitShowPoint(JsonElement row) => _combat.HitShowPoint(row);
    private Task ToPlayerAndViewersAsync(GameSession s, uint msgId, IMessage message) => _combat.ToPlayerAndViewersAsync(s, msgId, message);
    private Task SendMonsterTimelineAsync(MonsterUnit monster, UseInSkillInfo info, bool highLevel = false, bool extra = false) =>
        _combat.SendMonsterTimelineAsync(monster, info, highLevel, extra);
    private Task BossCooldownStartedAsync(MonsterUnit boss, int skillId, long endMs) => _combat.BossCooldownStartedAsync(boss, skillId, endMs);
    private Task SendHitBulletsAsync(IEnumerable<GameSession> viewers, JsonElement row, SkillHitInfo hit, long masterId) =>
        _combat.SendHitBulletsAsync(viewers, row, hit, masterId);
    private Task RecordDamageStatisticAsync(GameSession source, DamageStatistics entry) => _combat.RecordDamageStatisticAsync(source, entry);
    private static int ChantMilliseconds(GameData data, JsonElement row, StatSheet stats) => CombatService.ChantMilliseconds(data, row, stats);
    private static double HealCritRate(long cri, long critChance, long skillCri) => CombatService.HealCritRate(cri, critChance, skillCri);
    private static double HealCritMultiplier(long critDamage) => CombatService.HealCritMultiplier(critDamage);
    private static long ModifiedHealing(StatSheet healer, StatSheet target, long amount) => CombatService.ModifiedHealing(healer, target, amount);
    private StatSheet HealerAttributes(GameSession s) => _combat.HealerAttributes(s);
    private static bool Airborne(GameSession p) => CombatService.Airborne(p);
    private static long ScaleMonsterDamage(long damage, int ratio) => CombatService.ScaleMonsterDamage(damage, ratio);
    private long MonsterBuffDamage(MonsterUnit monster, JsonElement? row, long damage) => _combat.MonsterBuffDamage(monster, row, damage);
    private static float Distance(Vec3 a, Vec3 b) => CombatService.Distance(a, b);
    private static float PlayerDistance(GameSession a, GameSession b) => CombatService.PlayerDistance(a, b);
    private static double[] Numbers(JsonElement row, string field) => CombatService.Numbers(row, field);
    private static IEnumerable<JsonElement> Tuples(JsonElement row, string field) => CombatService.Tuples(row, field);
    private static string Script(JsonElement row) => CombatService.Script(row);
    private static float Range(JsonElement row) => CombatService.Range(row);
    private static bool IsResurrection(JsonElement row) => CombatService.IsResurrection(row);
    private static IEnumerable<JsonElement> HostileBuffs(JsonElement row) => CombatService.HostileBuffs(row);
    private bool CanPlayerHit(GameSession source, GameSession target) => _combat.CanPlayerHit(source, target);
    private bool CanHitMonster(GameSession source, MonsterUnit target) => _combat.CanHitMonster(source, target);
    private bool IsPlayerHostile(GameSession a, GameSession b) => _combat.IsPlayerHostile(a, b);
    private GameSession[] EventAreaTargets(GameSession source, JsonElement row) => _combat.EventAreaTargets(source, row);
    private GameSession? EventTarget(GameSession source, long id) => _combat.EventTarget(source, id);
    private MonsterUnit? FindMonster(long sceneId, long uid) => _combat.FindMonster(sceneId, uid);
    private GameSession? FindPlayer(GameSession s, long id, bool dead = false) => _combat.FindPlayer(s, id, dead);
    private bool IsSameParty(GameSession a, GameSession b) => _combat.IsSameParty(a, b);
    private bool IsMonsterTargetAllowed(GameSession s, MonsterUnit m) => _combat.IsMonsterTargetAllowed(s, m);
    private (ICombatRule Rule, int Result)? TryAdmitOwnSkill(GameSession session, int skillId) => _combat.TryAdmitOwnSkill(session, skillId);
    private Task<(long Damage, long Hp, long Effective, long ShieldDamage)?> CommitPlayerDamageAsync(GameSession target, long damage,
        JsonElement? row, Func<bool>? valid = null, int sourceElement = 1, bool directHit = false) =>
        _combat.CommitPlayerDamageAsync(target, damage, row, valid, sourceElement, directHit);
    private Task PlayerDiesAsync(GameSession p) => _combat.PlayerDiesAsync(p);
    private Task HitEventPlayersAsync(GameSession source, GameSession[] targets, JsonElement row, int skillId, int showPoint,
        long castId, Func<bool> current, Action<long, long>? recordDamage = null, Vec3? center = null) =>
        _combat.HitEventPlayersAsync(source, targets, row, skillId, showPoint, castId, current, recordDamage, center);
    private Task CancelCastAsync(GameSession s, SkillInterruptedInfo.Types.SkillInterruptType why) => _combat.CancelCastAsync(s, why);
    private Task BreakForbiddenCastAsync(GameSession s) => _combat.BreakForbiddenCastAsync(s);
    private Task EndReadingAsync(GameSession s, ActiveSkillCast action) => _combat.EndReadingAsync(s, action);
    private bool ReturnsToShadow(GameSession s, JsonElement row) => _combat.ReturnsToShadow(s, row);
    private Task ReturnToShadowAsync(GameSession s, ActiveSkillCast action) => _combat.ReturnToShadowAsync(s, action);
    private void LeaveShadow(GameSession s, ActiveSkillCast action) => _combat.LeaveShadow(s, action);
    private Task<bool> StartChannelAsync(GameSession s, ActiveSkillCast action) => _combat.StartChannelAsync(s, action);
    private Task EnergyReleaseAsync(GameSession s, ActiveSkillCast action) => _combat.EnergyReleaseAsync(s, action);
    private Task HighEnergyTakeOffAsync(GameSession s, ActiveSkillCast action) => _combat.HighEnergyTakeOffAsync(s, action);
    private Task<JsonElement> CrossRipperRowAsync(GameSession s, JsonElement row) => _combat.CrossRipperRowAsync(s, row);
    private static ActiveSkillCast WithRow(ActiveSkillCast action, JsonElement row) => CombatService.WithRow(action, row);
    private int SonicBlowBranch(GameSession s, MonsterUnit target, JsonElement row) => _combat.SonicBlowBranch(s, target, row);
    private int DamageBranch(GameSession s, MonsterUnit? target, JsonElement row) => _combat.DamageBranch(s, target, row);
    private bool Relation(JsonElement row, bool friendly, bool self = false, int race = 0, bool dead = false) =>
        _combat.Relation(row, friendly, self, race, dead);
    private Task ApplySkillBuffAsync(StatSheet casterStats, GameSession recipient, int buffId, int durationMs, long sourceId) =>
        _combat.ApplySkillBuffAsync(casterStats, recipient, buffId, durationMs, sourceId);
    private static bool ComesFromALoop(string script, int buff) => CombatService.ComesFromALoop(script, buff);
    private SkillConfig? LearnedPassive(GameSession session, int group) => _combat.LearnedPassive(session, group);
    private static double ParseDesc(string text) => CombatService.ParseDesc(text);
    private Task DamageTowerPetAsync(MonsterUnit monster, TowerPetUnit pet, JsonElement? row, int skillId, long castId,
        int showPoint, int damagePercent = 100) =>
        PetCombat.DamageTowerPetAsync(monster, pet, row, skillId, castId, showPoint, damagePercent);
    private Task DamageCompanionAsync(MonsterUnit monster, CompanionUnit companion, JsonElement? row, int skillId,
        long castId, int showPoint, int damagePercent = 100) =>
        PetCombat.DamageCompanionAsync(monster, companion, row, skillId, castId, showPoint, damagePercent);
    private void ClearEventAttack(GameSession s) => _combat.ClearEventAttack(s);
    private static bool SkillAreaContains(JsonElement row, Vec3 origin, Vec3 requested, float face, Vec3 point) =>
        PetCombatService.SkillAreaContains(row, origin, requested, face, point);
    private void ReleaseDeathPublication(GameSession target) => _combat.ReleaseDeathPublication(target);
}
