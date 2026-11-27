using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Effective deployed-totem skills. The TotemService owns selection/superiority; this class owns the seven
/// SpecialEffectConfig operations used by those skills and the source lifetime of opcode-2 passive buffs.
/// BuffConfig's referenced passive rows have zero time and must be registered, not put on a 60-second timer.
/// </summary>
public sealed class TotemEffects : IGameTicker
{
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly MonsterService _monsters;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly ConditionalWeakTable<GameSession, Runtime> _runtime = [];
    private long _nextShieldSweepMs;
    [ThreadStatic] private static bool _calculatingHpCondition;

    private sealed class Runtime
    {
        public readonly Dictionary<(long Source, int Effect), long> Cooldowns = [];
        public readonly Dictionary<long, Shield> Shields = [];
    }
    private sealed record Shield(long Amount, long ExpireMs, int Stage);
    private sealed record Passive(InvItem Item, int Skill, int Id, BuffConfig Row);

    // BuffConfig passive -> on-cast temporary stack buff. These pairs are corroborated by each active row's
    // _kBuffCategory and its description, not merely adjacent numeric IDs.
    private static readonly IReadOnlyDictionary<int, int> CastStacks = new Dictionary<int, int>
    {
        [2500101] = 2500301, [2500201] = 2500401, [2500501] = 2500701, [2500601] = 2500801,
        [2500901] = 2501101, [2501001] = 2501201, [2501301] = 2501501, [2501401] = 2501601,
    };
    private static readonly HashSet<int> TimedStackIds = [.. CastStacks.Values];
    private static readonly IReadOnlyDictionary<int, int> StackSources = CastStacks.ToDictionary(p => p.Value, p => p.Key);
    private static readonly IReadOnlyDictionary<int, int> ProcSources = new Dictionary<int, int>
    {
        [2502101] = 2501901, [2502501] = 2502301,
        [2503401] = 2503201, [2513401] = 2513201, [2514901] = 2514701, [2506101] = 2505901,
        [2516701] = 2516501, [2508501] = 2508301,
    };

    public TotemEffects(GameData data, CharacterService character, BuffService buffs, CombatService combat,
        MonsterService monsters, SessionRegistry sessions, GameLoop loop)
    {
        this._data = data; this._character = character; this._buffs = buffs;
        this._monsters = monsters; this._sessions = sessions;
        this._loop = loop;
        character.DynamicProperties.Subscribe(ShieldProperties);
        combat.CastLanded.Subscribe(OnCastAsync);
        combat.DamageAdjusters.Subscribe(AdjustDamage);
        combat.ExtraHitDamage.Subscribe(ExtraDamage);
        combat.DealtAsync.Subscribe(OnDealtAsync);
        combat.ExtraHeal.Subscribe(ExtraHealing);
        combat.IncomingDamageAdjusters.Subscribe(AdjustIncomingDamageAsync);
        combat.TakenAsync.Subscribe(OnTakenAsync);
        combat.PlayerRevived.Subscribe(RefreshAsync);
    }

    private static double ParseNumber(string[] list, int index) =>
        index < list.Length && double.TryParse(list[index], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;

    private IEnumerable<(InvItem Item, int Skill, TotemSkillConfig Row)> Effective(GameSession s)
    {
        if (s.Profile.Totems?.TotemPlans.TryGetValue(s.Profile.Totems.CurrentPlan, out var plan) != true) yield break;
        var equipped = plan.SetUpTotem.Values.Where(g => g != 0)
            .Select(g => s.Items.FirstOrDefault(i => i.Guid == g && TotemService.IsTotem(_data, i.Entry)))
            .OfType<InvItem>().ToArray();
        var all = equipped.SelectMany(TotemService.Skills).ToHashSet();
        var used = new HashSet<int>();
        foreach (var item in equipped)
            foreach (int skill in TotemService.Skills(item))
            {
                if (TotemSkillConfig.Get(_data, skill) is not { } row) continue;
                bool suppressed = false;
                var chain = new HashSet<int>();
                for (int upper = (int)row.SuperiorSkillId; upper > 0 && chain.Add(upper);)
                {
                    if (all.Contains(upper)) suppressed = true;
                    upper = TotemSkillConfig.Get(_data, upper) is { } next ? (int)next.SuperiorSkillId : 0;
                }
                if (suppressed || (row.IsCoexist == 0 && !used.Add(skill))) continue;
                yield return (item, skill, row);
            }
    }
    private IEnumerable<Passive> Passives(GameSession s)
    {
        foreach (var (item, skill, row) in Effective(s))
            foreach (long id in row.SpecialAttribute)
                if (SpecialEffectConfig.Get(_data, id) is { } effect)
                    foreach (var op in effect.SpecialEffect)
                        if (op.Length == 2 && op[0] == 2 && BuffConfig.Get(_data, op[1]) is { } buff)
                            yield return new Passive(item, skill, (int)op[1], buff);
    }

    /// <summary>Skill attributes only. TotemService adds item roll attributes and applies effective-skill filtering.</summary>
    public IEnumerable<(long Id, double Value)> Attributes(GameSession s, InvItem item, int skill)
    {
        if (TotemSkillConfig.Get(_data, skill) is not { } row) yield break;
        foreach (var attr in GameData.Pairs(row.BasicAttribute)) yield return attr;
        foreach (long id in row.SpecialAttribute)
        {
            if (SpecialEffectConfig.Get(_data, id) is not { } effect) continue;
            var ops = effect.SpecialEffect;
            foreach (var op in ops)
            {
                if (op.Length == 3 && op[0] == 1) yield return (op[1], op[2]);
                else if (op.Length == 4 && op[0] == 7 && op[1] > 0)
                    yield return (op[2], TotemService.Skills(item).Count() / op[1] * op[3]);
                else if (op.Length == 3 && op[0] == 9)
                    yield return (op[1], Math.Floor(s.Progress!.Level * op[2] / 10000d));
                else if (op.Length == 7 && op[0] == 10 && !_calculatingHpCondition)
                {
                    // Stats() asks this provider for attributes; recurse once with HP conditions disabled to obtain
                    // the authoritative maximum. None of the totem op-10 rows changes MaxHP.
                    _calculatingHpCondition = true;
                    long maxHp;
                    try { maxHp = _character.Stats(s)[101]; }
                    finally { _calculatingHpCondition = false; }
                    long hp = s.CurrentHp ?? maxHp;
                    s.StatsReadHp = true; // the memoized sheet now depends on HP (CharacterStatsService.Stats)
                    long q = op[2] == 1 ? maxHp > 0 ? (long)Math.Floor(hp * 10000d / maxHp) : 0 : hp;
                    bool active = op[1] switch
                    {
                        1 => q > op[3],
                        2 => q >= op[3],
                        3 => q < op[3],
                        4 => q <= op[3],
                        5 => q == op[3],
                        _ => false
                    };
                    if (active) yield return (op[4], op[5] == 2 ? Math.Floor(s.Progress!.Level * op[6] / 10000d) : op[6]);
                }
                else if (op.Length >= 8 && op[0] == 12 && op[1] == 1 && op[3] == 2)
                {
                    // The five current totem tuples all add the sixth operand per active floor to the attribute
                    // supplied by their adjacent op-1. Category limits the counted buff to this totem instance.
                    long attr = ops.FirstOrDefault(x => x.Length == 3 && x[0] == 1)?[1] ?? 0;
                    int floors = StackCount(s, item.Guid, op[2]);
                    if (attr != 0 && floors > 0) yield return (attr, floors * op[5]);
                }
                else if (op.Length == 2 && op[0] == 2 && op[1] is >= 2530401 and <= 2530701)
                {
                    // These four op-2 passive descriptions specify an additional value per active Totem stack.
                    long attr = ops.FirstOrDefault(x => x.Length == 3 && x[0] == 1)?[1] ?? 0;
                    int floors = StackCount(s, item.Guid, 4001);
                    if (attr != 0 && floors > 0) yield return (attr, floors * (op[1] is 2530401 or 2530601 ? 50 : 100));
                }
            }
        }
    }

    private int StackCount(GameSession s, long source, long category) => s.Buffs.Where(b => b.IsTotemOwned && !b.IsPassive && b.SourceId == source && TimedStackIds.Contains(b.ConfigId)
        && BuffConfig.Get(_data, b.ConfigId) is { } row && row.BuffCategory.Contains(category)).Sum(b => b.PileCount);

    private int StackCap(GameSession s, InvItem item, int activeId)
    {
        if (BuffConfig.Get(_data, activeId) is not { } active) return 0;
        int cap = (int)Math.Max(1, active.MaxStack);
        foreach (var (_, _, row) in Effective(s).Where(e => e.Item.Guid == item.Guid))
            foreach (long id in row.SpecialAttribute)
                if (SpecialEffectConfig.Get(_data, id) is { } effect)
                    foreach (var op in effect.SpecialEffect)
                        if (op.Length == 3 && op[0] == 18 && active.BuffCategory.Contains(op[1])) cap += (int)op[2];
        return cap;
    }

    private double Bonus(GameSession s, long id)
    {
        var effective = Effective(s).ToArray();
        double skills = effective.SelectMany(e => Attributes(s, e.Item, e.Skill)).Where(a => a.Id == id).Sum(a => a.Value);
        var sources = effective.Select(e => e.Item.Guid).ToHashSet();
        double temporary = s.Buffs.Where(b => b.IsTotemOwned && !b.IsPassive && sources.Contains(b.SourceId))
            .SelectMany(b => b.Changes).Where(c => c.Attr == id).Sum(c => c.Value);
        return skills + temporary;
    }

    /// <summary>Only deployed-totem bonus components; base ATK/MATK, defense, and CRIT chance already enter Damage().</summary>
    public double OutgoingMultiplier(GameSession s, JsonElement skillRow, DamageHitResult result)
    {
        var skill = new SkillConfig(skillRow);
        double bonus = Magic(skill) ? Bonus(s, 213) : Bonus(s, 211)
            + Bonus(s, skill.DistanceType == 2 ? 229 : 227);
        double resultFactor = Math.Max(0, 1 + bonus / 10000d);
        if (result == DamageHitResult.Critical) resultFactor *= Math.Max(0, (15000 + Bonus(s, 209)) / 15000d);
        return resultFactor;
    }

    public double IncomingMultiplier(GameSession s, DamageHitResult result)
    {
        double factor = Math.Max(0, 1 - Bonus(s, 216) / 10000d);
        if (result == DamageHitResult.Critical) factor *= Math.Max(0, (15000 - Bonus(s, 210)) / 15000d);
        return factor;
    }

    public double HealingMultiplier(GameSession s) => Math.Max(0, 1 + Bonus(s, 207) / 10000d);

    /// <summary>Reconcile op-2 passive source GUIDs and their derived temporary stacks after a plan or skill change.</summary>
    public async Task RefreshAsync(GameSession s)
    {
        var wanted = Passives(s).Select(p => (p.Item.Guid, p.Id)).ToHashSet();
        var activeSources = wanted.Select(p => p.Guid).ToHashSet();
        var state = _runtime.GetOrCreateValue(s);
        foreach (var stale in s.Buffs.Where(b => b.IsTotemOwned && (b.IsPassive && !wanted.Contains((b.SourceId, b.ConfigId))
            || !b.IsPassive && StackSources.TryGetValue(b.ConfigId, out int stackSource)
                && !wanted.Contains((b.SourceId, stackSource))
            || !b.IsPassive && ProcSources.TryGetValue(b.ConfigId, out int sourcePassive)
                && !wanted.Contains((b.SourceId, sourcePassive)))).ToArray())
            await _buffs.RemoveAsync(s, stale);
        foreach (var (guid, id) in wanted)
            await _buffs.AddPassiveAsync(s, id, guid);
        lock (state) foreach (long source in state.Shields.Keys.Where(g => !activeSources.Contains(g)).ToArray()) state.Shields.Remove(source);
        // Login rebuilds the authoritative snapshot before ENTITYSELF; live changes also notify the unit.
        if (s.SceneLoading) _character.Refresh(s);
        else await _character.RefreshStatsAsync(s);
    }

    private IEnumerable<(int Id, long Value)> ShieldProperties(GameSession s)
    {
        var state = _runtime.GetOrCreateValue(s);
        long total;
        lock (state) total = state.Shields.Values.Where(x => x.ExpireMs > _loop.NowMs).Sum(x => x.Amount);
        yield return (2003, total);
    }

    public void Tick(GameTime time)
    {
        long now = _loop.NowMs;
        if (now < _nextShieldSweepMs) return;
        _nextShieldSweepMs = now + 500;
        foreach (var s in _sessions.All().Where(s => s.Role != null))
        {
            var state = _runtime.GetOrCreateValue(s);
            bool expired;
            lock (state)
            {
                var ids = state.Shields.Where(p => p.Value.ExpireMs <= now).Select(p => p.Key).ToArray();
                foreach (long id in ids) state.Shields.Remove(id);
                expired = ids.Length > 0;
            }
            if (expired) _loop.TrackLegacyDetached("totem shield expiration", () => PublishExpiredShieldAsync(s));
        }
    }

    private async Task PublishExpiredShieldAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null) return;
        try { await _character.RefreshStatsAsync(s); }
        catch (Exception e) when (e is IOException or ObjectDisposedException
            or Accounts.Persistence.PersistenceException or InvalidOperationException)
        { }
    }

    private static bool Magic(SkillConfig skill) => skill.DmgType == 2;
    private static bool Healing(SkillConfig skill) =>
        skill.DamageParam1 is [<= 0, ..] && skill.DamageParam2 is [> 0, ..];

    private static int BuffAttrBonus(BuffConfig buff) =>
        buff.BuffAttributeList is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0
        && list[0].ValueKind == JsonValueKind.Array && list[0].GetArrayLength() > 4
            ? (int)(GameData.Number(list[0][4]) ?? 0) : 0;

    private bool Proc(GameSession s, long source, int effect, int chanceBp, int cooldownMs)
    {
        if (chanceBp <= 0) return false;
        var state = _runtime.GetOrCreateValue(s);
        lock (state)
        {
            long now = _loop.NowMs;
            if (state.Cooldowns.GetValueOrDefault((source, effect)) > now || Random.Shared.Next(10000) >= chanceBp) return false;
            state.Cooldowns[(source, effect)] = now + cooldownMs;
            return true;
        }
    }

    private async Task OnCastAsync(GameSession s, JsonElement skillRow)
    {
        var skill = new SkillConfig(skillRow);
        foreach (var passive in Passives(s).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            if (!CastStacks.TryGetValue(passive.Id, out int activeId)) continue;
            if (passive.Id == 2501401 && !Healing(skill)) continue;
            int duration = passive.Row.LogicData is [var d, ..] ? (int)d : 0;
            if (duration <= 0) continue;
            await _buffs.AddStackAsync(s, activeId, duration, passive.Item.Guid, StackCap(s, passive.Item, activeId));
        }
    }

    private long AdjustDamage(GameSession s, MonsterUnit target, JsonElement skillRow, DamageHitResult result, long damage)
    {
        if (damage <= 0 || target.Template.MaxHp <= 0) return damage;
        bool magic = Magic(new SkillConfig(skillRow));
        foreach (var passive in Passives(s).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            if (magic && passive.Id is not (2503001 or 2513001)) continue;
            if (!magic && passive.Id is not (2514301 or 2505501)) continue;
            var logic = passive.Row.LogicData;
            if (logic.Length < 3) continue;
            long threshold = logic[0], bonus = logic[2];
            if (target.Hp * 100 <= target.Template.MaxHp * threshold)
                damage += (long)Math.Floor(damage * bonus / 10000d);
        }
        return (long)Math.Floor(damage * OutgoingMultiplier(s, skillRow, result));
    }

    private long ExtraDamage(GameSession s, MonsterUnit target, JsonElement skillRow, long baseDamage)
    {
        if (baseDamage <= 0) return 0;
        bool magic = Magic(new SkillConfig(skillRow));
        long extra = 0;
        foreach (var passive in Passives(s).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            if (magic && passive.Id is not (2504001 or 2514001)) continue;
            if (!magic && passive.Id is not (2515701 or 2507501)) continue;
            var logic = passive.Row.LogicData;
            var desc = passive.Row.DescData;
            int chance = logic.Length > 0 ? (int)logic[0] : 0;
            int cooldown = (int)(ParseNumber(desc, 2) * 1000);
            if (!Proc(s, passive.Item.Guid, passive.Id, chance, cooldown)) continue;
            double coefficient = ParseNumber(desc, 1);
            extra += (long)Math.Floor(_character.Stats(s)[magic ? 104 : 103] * coefficient / 100d);
        }
        return extra;
    }

    private async Task OnDealtAsync(GameSession s, MonsterUnit target, JsonElement skillRow, long damage)
    {
        if (damage <= 0) return;
        foreach (var passive in Passives(s).Where(p => p.Id == 2501901).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            var logic = passive.Row.LogicData;
            int chance = logic.Length > 0 ? (int)logic[0] : 0;
            int cooldown = logic.Length > 1 ? (int)logic[1] : 0;
            if (Proc(s, passive.Item.Guid, passive.Id, chance, cooldown)) await _buffs.AddSourcedAsync(s, 2502101, 5000, passive.Item.Guid);
        }
    }

    private long ExtraHealing(GameSession s, long heal)
    {
        if (heal <= 0) return heal;
        long extra = 0;
        foreach (var passive in Passives(s).Where(p => p.Id == 2515601).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            int chance = Passives(s).Where(p => p.Item.Guid == passive.Item.Guid && p.Id is 2530201 or 2530301)
                .Sum(p => BuffAttrBonus(p.Row));
            var desc = passive.Row.DescData;
            int cooldown = (int)(ParseNumber(desc, 2) * 1000);
            if (Proc(s, passive.Item.Guid, passive.Id, chance, cooldown))
            {
                double coefficient = ParseNumber(desc, 1);
                extra += (long)Math.Floor(_character.Stats(s)[104] * coefficient / 100d);
            }
        }
        foreach (var passive in Passives(s).Where(p => p.Id is 2504401 or 2514401).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            var logic = passive.Row.LogicData;
            int threshold = logic.Length > 0 ? (int)logic[0] : 0;
            if (_character.Stats(s)[2000] * 100 <= _character.Stats(s)[101] * threshold)
                extra += (long)Math.Floor(heal * (logic.Length > 2 ? logic[2] : 0) / 10000d);
        }
        return (long)Math.Floor((heal + extra) * HealingMultiplier(s));
    }

    private async Task<long> AdjustIncomingDamageAsync(MonsterUnit attacker, GameSession s, DamageHitResult result, long damage)
    {
        if (damage <= 0) return damage;
        damage = (long)Math.Floor(damage * IncomingMultiplier(s, result));
        if (damage <= 0) return 0;
        var state = _runtime.GetOrCreateValue(s);
        long oldShield;
        lock (state) oldShield = state.Shields.Values.Where(x => x.ExpireMs > _loop.NowMs).Sum(x => x.Amount);
        lock (state)
        {
            long now = _loop.NowMs;
            foreach (var (source, shield) in state.Shields.ToArray()) if (shield.ExpireMs <= now) state.Shields.Remove(source);
            foreach (var passive in Passives(s).Where(p => p.Id == 2509901).DistinctBy(p => (p.Item.Guid, p.Id)))
            {
                long source = passive.Item.Guid;
                if (!state.Shields.TryGetValue(source, out var shield))
                {
                    int chance = (int)(ParseNumber(passive.Row.DescData, 0) * 100);
                    foreach (var mod in Passives(s).Where(p => p.Item.Guid == source && p.Id is 2530001 or 2530101))
                        chance += BuffAttrBonus(mod.Row);
                    if (!Proc(s, source, passive.Id, chance, 10000)) continue;
                    long maximum = _character.Stats(s)[101];
                    shield = new Shield(Math.Max(1, maximum * 5 / 100), now + 5000, 0);
                }
                while (damage > 0 && shield.Amount > 0)
                {
                    long absorbed = Math.Min(damage, shield.Amount);
                    damage -= absorbed;
                    shield = shield with { Amount = shield.Amount - absorbed };
                    if (shield.Amount == 0 && shield.Stage == 0)
                        shield = new Shield(Math.Max(1, _character.Stats(s)[101] * 5 / 100), now + 5000, 1);
                }
                if (shield.Amount > 0) state.Shields[source] = shield;
                else state.Shields.Remove(source);
            }
        }
        long newShield;
        lock (state) newShield = state.Shields.Values.Where(x => x.ExpireMs > _loop.NowMs).Sum(x => x.Amount);
        if (newShield != oldShield) await _character.RefreshStatsAsync(s);
        return damage;
    }

    private async Task OnTakenAsync(MonsterUnit attacker, GameSession s, long damage)
    {
        if (damage <= 0 || s.Dead || s.CurrentHp is <= 0) return; // runs on every hit a player takes: no stat sheet for it
        foreach (var passive in Passives(s).Where(p => p.Id == 2502301).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            var desc = passive.Row.DescData;
            long max = _character.Stats(s)[101], hp = _character.Stats(s)[2000];
            if (max > 0 && hp * 100 < max * ParseNumber(desc, 0)
                && Proc(s, passive.Item.Guid, passive.Id, 10000, (int)(ParseNumber(desc, 3) * 1000)))
                await _buffs.AddSourcedAsync(s, 2502501, (int)(ParseNumber(desc, 2) * 1000), passive.Item.Guid);
        }
        foreach (var passive in Passives(s).Where(p => p.Id == 2501301).DistinctBy(p => (p.Item.Guid, p.Id)))
            if (StackCount(s, passive.Item.Guid, 4005) > 0 && Proc(s, passive.Item.Guid, passive.Id, 1000, 15000))
            {
                await _monsters.ApplySlowAsync(attacker, 1000, 5000);
                await _buffs.ShowOnUnitAsync(attacker, 2501701, 5000, s.Role!.Id);
            }
    }

    /// <summary>Player-vs-player kill callback for the four player-kill passives; not called for monster kills.</summary>
    public async Task OnPlayerKilledAsync(GameSession s)
    {
        foreach (var passive in Passives(s).Where(p => p.Id is 2503201 or 2513201 or 2514701 or 2505901).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            int active = passive.Id switch
            {
                2503201 => 2503401,
                2513201 => 2513401,
                2514701 => 2514901,
                _ => 2506101
            };
            var desc = passive.Row.DescData;
            if (Proc(s, passive.Item.Guid, passive.Id, 10000, (int)(ParseNumber(desc, 2) * 1000)))
                await _buffs.AddSourcedAsync(s, active, (int)(ParseNumber(desc, 1) * 1000), passive.Item.Guid);
        }
    }

    /// <summary>Player-vs-player incoming-hit callback for the two recovery passives.</summary>
    public async Task OnPlayerDamagedAsync(GameSession s)
    {
        foreach (var passive in Passives(s).Where(p => p.Id is 2516501 or 2508301).DistinctBy(p => (p.Item.Guid, p.Id)))
        {
            var desc = passive.Row.DescData;
            if (Proc(s, passive.Item.Guid, passive.Id, 10000, (int)(ParseNumber(desc, 2) * 1000)))
                await _buffs.AddSourcedAsync(s, passive.Id == 2516501 ? 2516701 : 2508501,
                    (int)(ParseNumber(desc, 1) * 1000), passive.Item.Guid);
        }
    }
}
