using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Socketed cards' Buff(2) special effects (CardConfig._kSkillAttribute -> SpecialEffectConfig._kSpecialEffect [2, buff],
/// 304 of the 348 card operations; Attr(1) and Attr1CountAddAttr2(8) are EquipmentStunts'). Every referenced BuffConfig
/// row is a zero-time passive (iPassiveBuff 1): like totem passives they are registered per source (the card GUID) for
/// as long as the copy is socketed in an active socket (EquipmentStunts.ActiveCards), which publishes their icons, applies their _kChangeAttri (Variable CT, 2024001..4)
/// and arms BuffService's lethal-hit revive for groups 20520/20521 (2052001.., 2052101..). (inferred) Their
/// _iSceneClear removes them on a scene change; the loaded scene registers them again, carrying the revive cooldown.
///
/// Executed behaviours (data-settled families; the trigger timing "when dealing damage" is read from the
/// descriptions and applied to every damaging hit on a monster, inferred):
/// - status on hit: a base passive with _kLogicData [0, status buff, ms] (2000001 Stun 901601 500 ms, Blind, Curse,
///   Immobilize, Sleep, Freeze, Silence) gains chance from every passive whose _kBuffAttributeList row
///   ["0", base group, "4", "1", chance, "0"] names its group ("Chance stacks with multiple cards"); the description's
///   third value is the internal cooldown in seconds. The status lands as a monster unit buff, as skill _kBuff2 does.
/// - life steal 20170 (2017101..): chance from the same modifier rows, heal = desc[1]% of the damage up to desc[2],
///   internal cooldown desc[3] seconds.
/// - SP absorb 2050401: _kLogicData [chance bp, ...], desc[1]% of the damage as SP.
/// - size: 20501 (2050101..) adds _kLogicData[0] bp of damage against Small/Medium/Large (NPCConfig._iSize 1..3)
///   monsters; 20500 (2050001..) adds _kLogicData[1] bp with a _kLogicData[0] bp chance.
/// - Splash: 20508 (Shadow Card: Baphomet, 2050801..) and 20509 (Card: Baphomet, 2050901..), see <see cref="SplashOf"/>.
/// - monster family: Orc Lady, Kobold Leader, Goblin Leader, Aster, Crab, Shellfish add _kLogicData[0] bp of damage against
///   monsters with their NPCConfig._kNpcTag, see <see cref="FamilyTag"/>.
/// - autocast on a Physical attack: 20190 Bash, 20200 Jupitel Thunder, see <see cref="AutocastsAsync"/>.
/// Not executed (no machinery or no data link): the Divine Light autocast, armor element changes, magic nullify/reflect,
/// status immunities, Endure, cooldown reset, monster SP drain, Double Attack, detection, indestructible gear.
/// Skill(3) grants (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind) go into RoleProgress.GrantedSkills.
/// </summary>
public sealed class CardEffects
{
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly SkillService _skills;
    private readonly CombatService _combat;
    private readonly ConditionalWeakTable<GameSession, Runtime> _runtime = [];

    private sealed class Runtime
    {
        /// <summary>Last registered buff GUID per (card GUID, config), to carry BuffEffectCds over a re-registration.</summary>
        public readonly Dictionary<(long Source, int Config), long> Registered = [];
        public readonly Dictionary<long, long> Cooldowns = []; // proc family (base buff group) -> ready at
    }

    private sealed record Passive(InvItem Card, int Id, JsonElement Row);

    private const long SkillOp = 3;
    private const int LifeStealGroup = 20170, SpAbsorb = 2050401, SizeAlways = 20501, SizeChance = 20500;
    private const int SplashChance = 20508, SplashAlways = 20509;
    private const int BashAutocast = 20190, JupitelAutocast = 20200;
    private const long Melee = 1; // SkillConfig._iDistanceType (G_ClientCfg.SkillDistanceType: Remote is 2)
    private const long PhysicalDamage = 1; // SkillConfig._iDmgType

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public CardEffects(GameData data, CharacterService character, BuffService buffs, CardService cards, CombatService combat,
        SceneService scenes, SkillService skills)
    {
        this._data = data;
        this._skills = skills;
        this._character = character;
        this._buffs = buffs;
        this._combat = combat;
        cards.SocketsChanged.Add(RefreshAsync);
        character.WornGearChanged.Add(RefreshAsync);
        scenes.Loaded.Add(RefreshAsync);
        combat.DamageAdjusters.Add(AdjustDamage);
        combat.DealtAsync.Add(OnDealtAsync);
        combat.CastResolved.Add(OnCastResolvedAsync);
    }

    private IEnumerable<Passive> Passives(GameSession s)
    {
        foreach (var (card, id) in EquipmentStunts.CardEffects(s, _data))
            if (_data.Row("SpecialEffectConfig", id) is { } effect)
                foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect"))
                    if (op.Length == 2 && op[0] == 2 && _data.Row("BuffConfig", op[1]) is { } buff)
                        yield return new Passive(card, (int)op[1], buff);
    }

    /// <summary>The passives currently registered for this session's socketed cards.</summary>
    internal IReadOnlyList<(long Source, int Config)> Registered(GameSession s) =>
        s.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned).Select(b => (b.SourceId, b.ConfigId)).ToList();

    /// <summary>Reconcile card-owned passives with the socketed copies (insert, remove, transfer, plan switch,
    /// star-up, worn gear, scene load).</summary>
    public async Task RefreshAsync(GameSession s)
    {
        if (s.Role == null) return;
        var wanted = Passives(s).Select(p => (Source: p.Card.Guid, p.Id)).ToHashSet();
        var state = _runtime.GetOrCreateValue(s);
        bool statsChanged = false;
        foreach (var stale in s.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned && !wanted.Contains((b.SourceId, b.ConfigId))).ToArray())
        {
            statsChanged |= stale.Changes.Count > 0;
            state.Registered.Remove((stale.SourceId, stale.ConfigId));
            await _buffs.RemoveAsync(s, stale);
        }
        foreach (var (source, id) in wanted)
        {
            if (s.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == source && b.ConfigId == id)) continue;
            await _buffs.AddPassiveAsync(s, id, source, totemOwned: false);
            if (s.Buffs.FirstOrDefault(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == source && b.ConfigId == id) is not { } added)
                continue;
            statsChanged |= added.Changes.Count > 0;
            // A scene change cleared the previous registration; its internal cooldown stays with the card.
            if (state.Registered.TryGetValue((source, id), out long previous) && s.BuffEffectCds.Remove(previous, out long readyAt))
                s.BuffEffectCds[added.Guid] = readyAt;
            state.Registered[(source, id)] = added.Guid;
        }
        await GrantSkillsAsync(s);
        if (!statsChanged) return;
        if (s.SceneLoading) _character.Refresh(s);
        else await _character.RefreshStatsAsync(s);
    }

    /// <summary>SpecialEffectConfig op Skill(3) [3, SkillConfig row] (enum_SpecialEffectType.Skill): the socketed card grants
    /// that row's group at its level while it stays socketed (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind;
    /// _iSystemType 3, enum_SkillSystemType.CardSkill). The skill panel lists them as card skills and they can be loaded
    /// and cast; the highest level of a group counts.</summary>
    private async Task GrantSkillsAsync(GameSession s)
    {
        if (s.Progress is not { } progress) return;
        Dictionary<int, int> granted = [];
        foreach (var (_, id) in EquipmentStunts.CardEffects(s, _data))
            if (SpecialEffectConfig.Get(_data, id) is { } effect)
                foreach (var op in effect.SpecialEffect)
                    if (op is [SkillOp, var row] && SkillConfig.Get(_data, row) is { SkillId: > 0 } skill)
                        granted[(int)skill.SkillId] = Math.Max(granted.GetValueOrDefault((int)skill.SkillId), (int)skill.Level);
        if (granted.Count == progress.GrantedSkills.Count && granted.All(g => progress.GrantedSkills.GetValueOrDefault(g.Key) == g.Value)) return;
        progress.GrantedSkills.Clear();
        foreach (var (group, level) in granted) progress.GrantedSkills[group] = level;
        if (s.SkillPlans != null) await _skills.SyncAsync(s);
    }

    private static long[] Numbers(JsonElement row, string field) => row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))] : [];

    private static double[] Description(JsonElement row) => row.TryGetProperty("_kDescData", out var list) && list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => GameData.Number(v) ?? 0)] : [];

    /// <summary>_kBuffAttributeList ["0", group, "4", "1", value, "0"]: the passive adds value to field 4 (the trigger
    /// chance, basis points) of the passive group named.</summary>
    private static IEnumerable<(long Group, long Value)> ChanceModifiers(JsonElement row)
    {
        if (!row.TryGetProperty("_kBuffAttributeList", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 5) continue;
            var values = entry.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            if (values[2] == 4 && values[3] == 1) yield return (values[1], values[4]);
        }
    }

    private bool Ready(GameSession s, long family, long cooldownMs)
    {
        var state = _runtime.GetOrCreateValue(s);
        long now = MonsterService.NowMs;
        lock (state)
        {
            if (state.Cooldowns.GetValueOrDefault(family) > now) return false;
            if (cooldownMs > 0) state.Cooldowns[family] = now + cooldownMs;
            return true;
        }
    }

    /// <summary>Base proc passives and the chance / cooldown their modifiers give them.</summary>
    private IEnumerable<(JsonElement Base, long Group, long Chance, double[] Description)> Procs(IReadOnlyList<Passive> passives)
    {
        var modifiers = passives.SelectMany(p => ChanceModifiers(p.Row).Select(m => (m.Group, m.Value, Description: Description(p.Row)))).ToList();
        foreach (var basis in passives.DistinctBy(p => p.Id))
        {
            long group = GameData.Int(basis.Row, "_iBuffID");
            var own = modifiers.Where(m => m.Group == group).ToList();
            if (own.Count == 0) continue;
            yield return (basis.Row, group, own.Sum(m => m.Value), own.OrderByDescending(m => m.Description.Length).First().Description);
        }
    }

    internal async Task OnDealtAsync(GameSession s, MonsterUnit target, JsonElement skillRow, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead) return;
        var passives = Passives(s).ToList();
        if (passives.Count == 0) return;
        foreach (var (basis, group, chance, desc) in Procs(passives))
        {
            var logic = Numbers(basis, "_kLogicData");
            if (group == LifeStealGroup)
            {
                // "has a @{1}% Chance to restore Life equal to @{2}% of the damage dealt, up to @{3}. Cooldown @{4} sec."
                if (desc.Length < 4 || Roll(10000) >= chance || !Ready(s, group, (long)(desc[3] * 1000))) continue;
                long heal = Math.Min((long)desc[2], (long)Math.Floor(damage * desc[1] / 100));
                if (heal <= 0) continue;
                var stats = _character.Stats(s);
                await _character.SetHpSpAsync(s, stats[2000] + heal, null);
            }
            else if (logic.Length == 3 && logic[0] == 0 && logic[2] > 0 && _data.Row("BuffConfig", logic[1]) is not null)
            {
                // "has a @{1}% Chance to inflict a status for @{2} sec. Internal Cooldown: @{3} sec."
                if (target.Dead || target.Hp <= 0) continue;
                long cooldown = desc.Length >= 3 ? (long)(desc[2] * 1000) : 0;
                if (Roll(10000) >= chance || !Ready(s, group, cooldown)) continue;
                await _buffs.ShowOnUnitAsync(target, (int)logic[1], (int)logic[2], s.Role.Id);
            }
        }
        foreach (var passive in passives.Where(p => p.Id == SpAbsorb).DistinctBy(p => p.Id))
        {
            // "has a @{1}% Chance to absorb @{2}% of the damage dealt to the enemy as SP."
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 1 || desc.Length < 2 || Roll(10000) >= logic[0]) continue;
            long sp = (long)Math.Floor(damage * desc[1] / 100);
            if (sp <= 0) continue;
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, null, stats[2001] + sp);
        }
    }

    /// <summary>One Splash passive's numbers. <paramref name="Chance"/> is in basis points, <paramref name="Ratio"/> too, of
    /// the triggering hit's damage.</summary>
    internal readonly record struct Splash(long Chance, long CooldownMs, float Radius, int Extra, long Ratio, long Effect);

    /// <summary>
    /// "Melee Basic Attacks [have a @{1}% Chance to] trigger a Splash effect within a @{2}-meter radius. (This Splash does
    /// not count as a Normal Attack effect.) Affects up to @{3} Extra targets, with damage reduced to @{4}% of the original.
    /// Internal Cooldown: @{5} seconds." The buff's _kLogicData is [chance bp, proc row] (20508) or [proc row] (20509), and
    /// its _kDescData ends with the cooldown. The proc row (_kScriptLogic "Jianshe", "Splash - Card") has _kLogicData
    /// [effect, radius cm], _iTargetMax the extra targets and _kDamageParamExtra[0]'s last value the ratio
    /// (T_0addAXY_XisExtra_YisAttri). Effects 2050802 and 2051102 are one prefab at scale 1 and 1.67, as 3 m and 5 m.
    /// </summary>
    internal static Splash? SplashOf(BuffConfig buff, SkillConfig row)
    {
        if (buff.BuffId is not (SplashChance or SplashAlways) || row.LogicData is not [var effect, var radius]
            || row.DamageParamExtra is not [[.., var ratio], ..] || buff.DescData is not [.., var cooldown]
            || !double.TryParse(cooldown, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return null;
        return new Splash(buff.LogicData.Length >= 2 ? buff.LogicData[0] : 10000, (long)(seconds * 1000), radius / 100f,
            (int)row.TargetMax, ratio, effect);
    }

    /// <summary>
    /// A melee normal attack that hit (31101 is the server's: the client asks only for the locked target, 31102, and shows the
    /// dsts it is told, LC_Scene_Sync._skillHit). The rest is (inferred) from the text and the row: the damage is the hit's
    /// on the locked target, "the original" (the formula's Extra; the row has no ratio of its own), so each extra target
    /// takes that share with no defense or element of its own; the area centres on the locked target, dead or alive; "Extra"
    /// leaves out what the swing itself hit; the extra cast triggers no CastResolved ("does not count as a Normal Attack
    /// effect"); each card has its own cooldown, which an empty area also starts; the gfx plays only when something is hit.
    /// </summary>
    private async Task SplashAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed,
        SkillHitInfo hit, IReadOnlyList<Passive> passives)
    {
        if (processed.Count == 0 || cast.DistanceType != Melee || !cast.IsNormalAttack(_data)) return;
        var primary = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? processed[0];
        if (hit.Dsts.FirstOrDefault(d => d.DstID == primary.Uid) is not { Damage: > 0 } struck) return;
        foreach (var passive in passives)
        {
            var buff = new BuffConfig(passive.Row);
            if (buff.BuffId is not (SplashChance or SplashAlways) || buff.LogicData is not [.., var id]
                || SkillConfig.Get(_data, id) is not { } row || SplashOf(buff, row) is not { } splash
                || Roll(10000) >= splash.Chance || !Ready(s, buff.BuffId, splash.CooldownMs)) continue;
            var extra = _combat.MonstersNear(s, primary.Pos, splash.Radius).Where(m => !processed.Contains(m)).Take(splash.Extra).ToList();
            if (extra.Count == 0) continue;
            await _combat.SendGfxAtAsync(s, primary, splash.Effect, row.Id);
            long damage = Math.Max(1, (long)Math.Floor(struck.Damage * splash.Ratio / 10000d));
            await _combat.CastExtraAsync(s, row.AsFixedHit(damage), extra);
        }
    }

    /// <summary>The procs that follow a player's own cast. CastResolved fires once per cast and never for an extra cast, so
    /// nothing here can trigger itself or another proc of this list.</summary>
    private async Task OnCastResolvedAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (s.Role == null) return;
        var passives = Passives(s).ToList();
        if (passives.Count == 0) return;
        var cast = new SkillConfig(action.Row);
        await SplashAsync(s, action, cast, processed, hit, passives);
        await AutocastsAsync(s, action, cast, processed, hit, passives);
    }

    /// <summary>Whether a cast sets off the autocast passive of this group.</summary>
    internal static bool TriggersAutocast(long group, long damageType) => group switch
    {
        BashAutocast or JupitelAutocast => damageType == PhysicalDamage,
        _ => false,
    };

    /// <summary>
    /// Bongun and Wind Ghost: "When making a Physical attack, has a ${1}% Chance to autocast [Bash] Lv.@{1}. Internal Cooldown:
    /// ${2} sec. Chance stacks with multiple cards." The base passive (2019001, 2020001) holds [0, the row]; the tier's passive
    /// adds the chance to its group and has [chance, cooldown] as _kDescData. An extra cast of the row pays nothing and starts
    /// no cooldown of its own. (inferred) A Physical attack is any own cast of an _iDmgType 1 row that reached a monster, a basic
    /// attack or a skill, a miss too, counted once per cast; the proc takes the locked target, or else the first the cast
    /// reached that is still alive, and with none it neither rolls nor starts its cooldown. The text gives no player target
    /// and an extra cast takes monsters only, so a hit on a player sets off nothing.
    /// </summary>
    private async Task AutocastsAsync(GameSession s, ActiveSkillCast action, SkillConfig cast, IReadOnlyList<MonsterUnit> processed,
        SkillHitInfo hit, IReadOnlyList<Passive> passives)
    {
        if (hit.Dsts.Count == 0) return;
        foreach (var (basis, group, chance, desc) in Procs(passives).ToList())
        {
            if (!TriggersAutocast(group, cast.DmgType) || desc.Length < 2 || new BuffConfig(basis).LogicData is not [_, var id]
                || SkillConfig.Get(_data, id) is not { } row) continue;
            var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId && m.Hp > 0) ?? processed.FirstOrDefault(m => m.Hp > 0);
            if (target == null || Roll(10000) >= chance || !Ready(s, group, (long)(desc[1] * 1000))) continue;
            await _combat.CastExtraAsync(s, row.Row, [target]);
        }
    }

    /// <summary>
    /// The NPCConfig._kNpcTag a family card's buff group counts on, 0 for any other. No table names the tags, so the link is
    /// (inferred) from who holds them: 1000 the six plain Orcs, leaving out Orc Lord and Orc Hero as "Damage against all Orcs
    /// (excluding Orc Lord and Orc Hero)" does; 1001 Kobolds; 1002 Goblins, with Rotar Zairo and Goblin Leader; 1003 Aster; 1004
    /// Crab ("Kara Crabs"); 1005 Shellfish. Only the tag counts, so the untagged copies of these monsters (24000xxx, 4940xxx)
    /// and the Kobold Leader do not.
    /// </summary>
    internal static long FamilyTag(long group) => group switch
    {
        >= 20150 and <= 20153 => 1000,
        >= 20220 and <= 20223 => 1001,
        20532 or 20533 => 1002,
        >= 20290 and <= 20293 => 1003,
        >= 20270 and <= 20273 => 1004,
        >= 20280 and <= 20283 => 1005,
        _ => 0,
    };

    /// <summary>"Ignores monster Size. Damage against Small, Medium, and Large monsters +X%" (2050101..4 always,
    /// 2050001..4 with a chance); size 0 monsters are unaffected (inferred from the three named sizes). "Damage against all
    /// Orcs +X%" and the other families (<see cref="FamilyTag"/>). None of these rows has a _kChangeAttri, so nothing of them is
    /// on the sheet DamageAttributes reads. (inferred) They add up with each other, as a group of damage terms does.</summary>
    internal long AdjustDamage(GameSession s, MonsterUnit target, JsonElement skillRow, DamageHitResult result, long damage)
    {
        if (damage <= 0) return damage;
        bool sized = target.Template.Size is >= 1 and <= 3;
        long[]? tags = null;
        long bonus = 0;
        foreach (var passive in Passives(s).DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            long group = GameData.Int(passive.Row, "_iBuffID");
            var logic = Numbers(passive.Row, "_kLogicData");
            if (group == SizeAlways && sized && logic.Length >= 1) bonus += logic[0];
            else if (group == SizeChance && sized && logic.Length >= 2 && Roll(10000) < logic[0]) bonus += logic[1];
            else if (FamilyTag(group) is not 0 and var family && logic.Length >= 1
                && (tags ??= NPCConfig.Get(_data, target.Template.NpcId)?.NpcTag ?? []).Contains(family)) bonus += logic[0];
        }
        return bonus <= 0 ? damage : damage + (long)Math.Floor(damage * bonus / 10000d);
    }
}
