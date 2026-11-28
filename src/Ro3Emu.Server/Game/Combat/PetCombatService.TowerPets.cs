using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class PetCombatService
{
    /// <summary>(inferred) Use the existing attribute-group calculator for a pet's configured stats and live buffs.</summary>
    internal StatSheet TowerPetStats(TowerPetUnit pet)
    {
        var raw = pet.BaseAttributes.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (id, value) in _buffs.UnitAttributes(pet)) raw[(int)id] = raw.GetValueOrDefault((int)id) + value;
        var stats = StatCalculator.Compute(raw, _data);
        stats[AttributeId.MaxHp] = Math.Max(1, stats[AttributeId.MaxHp]);
        stats[AttributeId.Hp] = pet.Hp;
        stats[AttributeId.Sp] = stats[AttributeId.MaxSp];
        stats[AttributeId.Level] = pet.Level;
        return stats;
    }

    internal void PopulateTowerPetBuffs(TowerPetUnit pet, EntityBase entity) => _buffs.PopulateEntity(pet, entity);

    private Task PublishTowerPetStatsAsync(WorldUnit unit)
    {
        if (unit is not TowerPetUnit pet) return Task.CompletedTask;
        var changed = new AOIAttrChange { EntityUuid = (ulong)pet.Uid };
        var stats = pet.CombatStats;
        // (inferred) Clear expired members too: a viewer can have received e.g. 1032 in a late AOI snapshot.
        foreach (int id in stats.Data.Keys.Union(pet.PublishedAttributeIds).Order().ToArray())
        {
            changed.Property.Add(new AoIProperty { Idx = id, Data = stats[id], BaseData = stats.Base.GetValueOrDefault(id) });
            pet.PublishedAttributeIds.Add(id);
        }
        return _world.ToUnitViewersAsync(pet, AoiAttrChange, changed);
    }

    private bool LiveTowerPet(GameSession owner, TowerPetUnit pet) => ReferenceEquals(owner, pet.Owner)
        && owner.Role != null && !owner.Dead && !owner.SceneLoading && !pet.Dead && pet.Hp > 0
        && owner.WorldScene == pet.WorldScene && owner.Instances.TowerAttempt is { Loaded: true, Settled: false } a
        && a.InstanceId == pet.InstanceId && a.Pets.Contains(pet);

    /// <summary>
    /// (verified) SkillConfig/Localization_en distinguish Bobo Bubble and the named allied heal/buff skills
    /// from attacks; _kBuff1 tuples give buff ID, chance and duration. (inferred) The owner and living tower
    /// pets are allies; select self then nearest recipients, capped by _iTargetMax, within _kRange[0].
    /// Original server AI and hit timings are unavailable; use the same 40%-action timing as tower attacks.
    /// </summary>
    public async Task TowerPetSupportAsync(GameSession owner, TowerPetUnit pet, int skillId)
    {
        if (!LiveTowerPet(owner, pet) || _data.Row("SkillConfig", skillId) is not { } row
            || PetCombatRules.SupportFor(row) is not { } support
            || pet.SupportSkills.FirstOrDefault(s => s.Id == skillId) is not { } skill
            || !pet.TryBeginCast(skill, Now)) return;
        var role = owner.Role;
        long castId = NextSessionId();
        int actionMs = pet.ActionMsFor(skill);
        var use = new UseInSkillInfo
        {
            SrcID = pet.Uid, SkillID = skillId, PetConfigId = pet.PetId, TargetId = pet.Uid,
            TargetPos = pet.Pos, Face = pet.Face, SessionId = castId, SkillMs = actionMs, StartTime = Now,
        };
        await _world.ToUnitViewersAsync(pet, UseInSkill, use);
        if (!pet.Viewers.Contains(owner)) await owner.SendGameAsync(UseInSkill, use);
        ScheduleRuntime(pet.WorldScene, TimeSpan.FromMilliseconds(actionMs * 0.4), async () =>
        {
            if (!LiveTowerPet(owner, pet) || !ReferenceEquals(role, owner.Role)) return;
            float radius = (float)(Numbers(row, "_kRange").FirstOrDefault() / 100);
            int maximum = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
            // (verified) Choco's target count is ${2}=10 in its description, while _iTargetMax=1.
            // (inferred) Prefer the explicit allied count for this conflicting row, rather than self-only casting.
            if (Script(row) == "PetSkill_1_QiaoKeHou") maximum = (int)Numbers(row, "_kDescData").ElementAtOrDefault(1);
            var recipients = owner.Instances.TowerAttempt!.Pets.Where(p => !p.Dead && p.Hp > 0 && p.WorldScene == pet.WorldScene
                    && (p == pet || !support.SelfOnly && MonsterService.Distance(pet.X, pet.Z, p.X, p.Z) <= radius))
                .Select(p => (Id: p.Uid, Pet: (TowerPetUnit?)p, Distance: MonsterService.Distance(pet.X, pet.Z, p.X, p.Z))).ToList();
            if (!support.SelfOnly && MonsterService.Distance(pet.X, pet.Z, owner.Position.X, owner.Position.Z) <= radius)
                recipients.Add((owner.Role!.Id, null, MonsterService.Distance(pet.X, pet.Z, owner.Position.X, owner.Position.Z)));
            var casterStats = pet.CombatStats;
            var hit = new SkillHitInfo
            {
                SrcID = pet.Uid, SkillId = skillId, SessionId = castId, SrcElement = pet.Element,
                ShowPoint = HealEffect(GameData.Int(row, "_iScriptShow"))
            };
            foreach (var recipient in recipients.OrderBy(r => r.Id == pet.Uid ? 0 : 1)
                         .ThenBy(r => r.Distance).ThenBy(r => r.Id).Take(maximum))
            {
                if (!LiveTowerPet(owner, pet) || !ReferenceEquals(role, owner.Role)) return;
                var stats = recipient.Pet?.CombatStats ?? _character.Stats(owner);
                foreach (var tuple in Tuples(row, "_kBuff1"))
                {
                    if (!RollBuff(tuple, out int id, out int duration) || _data.Row("BuffConfig", id) is not { } buff) continue;
                    long shield = PetCombatRules.AttributeAmount(buff, casterStats, stats);
                    if (recipient.Pet is { } ally) await _buffs.AddSupportAsync(ally, id, duration, pet.Uid, shield);
                    else await _buffs.AddSupportAsync(owner, id, duration, pet.Uid, shield);
                }
                long amount = support.Kind switch
                {
                    PetCombatRules.SupportKind.AttributeHeal => PetCombatRules.AttributeAmount(row, casterStats, stats),
                    PetCombatRules.SupportKind.MagicHeal => Math.Max(0, (long)Math.Floor(casterStats[AttributeId.Matk]
                        * Numbers(row, "_kDamageParam1").FirstOrDefault() / 10000
                        + Numbers(row, "_kDamageParam2").FirstOrDefault())),
                    _ => 0,
                };
                if (support.Kind == PetCombatRules.SupportKind.Buff) continue;
                long actual = Math.Min(amount, Math.Max(0, stats[AttributeId.MaxHp] - stats[AttributeId.Hp]));
                if (recipient.Pet is { } healed)
                {
                    healed.Hp += actual;
                    await _world.ToUnitViewersAsync(healed, AoiAttrChange, healed.HpChange());
                }
                else
                {
                    using var held = await CharacterService.Lock(owner);
                    if (owner.Dead || owner.SceneLoading || !ReferenceEquals(role, owner.Role)) return;
                    var current = _character.Stats(owner);
                    actual = Math.Min(amount, Math.Max(0, current[101] - current[2000]));
                    await _character.SetHpSpAsync(owner, current[2000] + actual, null);
                }
                hit.Dsts.Add(new DamageInfo { DstID = recipient.Id, Damage = actual, DamageType = DamageType.Heal });
                Healed.Invoke(owner, actual);
                await HealedAsync.InvokeAsync(owner, actual);
            }
            if (hit.Dsts.Count > 0)
                foreach (var viewer in pet.Viewers.Append(owner).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
        });
    }

    /// <summary>
    /// (verified) The support buffs change these AttributeConfig rows: per_res_dmg1 and per_res_dmg (Alice's reduction is
    /// 1216), per_beremote_dmg1 (Goat's ranged physical reduction), per_resatk_dmg1/per_resmatk_dmg1 and
    /// be_none_ele..be_undead_ele (Squidgitte's water resistance is be_water_ele). (inferred) Apply their live sum before
    /// shields; this is restricted to pet support contributions rather than changing all combat attributes.
    /// </summary>
    internal static long ApplyPetSupportResistance(long damage, IEnumerable<(long Id, double Value)> changes,
        JsonElement? skill, int fallbackElement)
    {
        if (damage <= 0) return damage;
        var values = changes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        double reduction = values.GetValueOrDefault(AttributeId.PerResDmg1) + values.GetValueOrDefault(AttributeId.PerResDmg);
        int element = fallbackElement;
        if (skill is { } row)
        {
            bool physical = GameData.Int(row, "_iDmgType") != 2;
            reduction += values.GetValueOrDefault(physical ? AttributeId.PerResAtkDmg1 : AttributeId.PerResMatkDmg1);
            if (GameData.Int(row, "_iDistanceType") == 2 && physical)
                reduction += values.GetValueOrDefault(AttributeId.PerBeRemoteDmg1);
            int configured = (int)GameData.Int(row, "_iEleType");
            if (configured > 1) element = configured;
        }
        if (element is >= 1 and <= AttributeId.ElementCount) reduction += values.GetValueOrDefault(AttributeId.BeNoneEle + element - 1);
        return Math.Max(0, (long)Math.Round(damage * Math.Clamp(1 - reduction / 10000, 0, 10)));
    }

    /// <summary>
    /// Tower pet skills use configured damaging PetStarConfig._kActiveSkill entries and the shared
    /// damage formula. Target choice and skill rotation are (inferred); 31100 source is the actor UUID,
    /// while petConfigId and 32809 keys remain the owned PetBaseConfig ID.
    /// </summary>
    public async Task TowerPetAttackAsync(GameSession owner, TowerPetUnit pet, MonsterUnit target, int skillId)
    {
        if (!ReferenceEquals(owner, pet.Owner) || pet.Dead || pet.Hp <= 0 || target.Dead || target.Hp <= 0 || owner.Role == null || owner.Dead || owner.SceneLoading
            || owner.Instances.TowerAttempt is not { Loaded: true, Settled: false } attempt
            || attempt.InstanceId != pet.InstanceId || target.InstanceId != pet.InstanceId
            || !attempt.Pets.Contains(pet) || !pet.AttackSkills.Any(s => s.Id == skillId)
            || MonsterService.Distance(pet.X, pet.Z, target.X, target.Z) > pet.AttackSkills.First(s => s.Id == skillId).Range + 1f
            || _data.Row("SkillConfig", skillId) is not { } row
            || owner.WorldScene != pet.WorldScene || target.WorldScene != pet.WorldScene
            || !pet.TryBeginCast(pet.AttackSkills.First(s => s.Id == skillId), Now)) return;
        var castingRole = owner.Role;
        var castingAttachment = owner.Attachment;
        long castingSceneGeneration = owner.SceneGeneration;
        long castId = NextSessionId();
        long now = Now;
        int duration = pet.ActionMsFor(pet.AttackSkills.First(s => s.Id == skillId));
        var use = new UseInSkillInfo
        {
            SrcID = pet.Uid, SkillID = skillId, TargetId = target.Uid, TargetPos = target.Pos,
            Face = pet.Face, SessionId = castId, SkillMs = duration, StartTime = now, PetConfigId = pet.PetId,
        };
        await _world.ToUnitViewersAsync(pet, UseInSkill, use);
        if (!pet.Viewers.Contains(owner)) await owner.SendGameAsync(UseInSkill, use);
        // The exact server pet hit point is unavailable. Schedule at 40% of the configured action (inferred).
        ScheduleRuntime(pet.WorldScene, TimeSpan.FromMilliseconds(duration * 0.4), async () =>
        {
            if (pet.Dead || pet.Hp <= 0 || owner.Dead || owner.SceneLoading || !ReferenceEquals(owner.Role, castingRole)
                || owner.Instances.TowerAttempt is not { Loaded: true, Settled: false } a
                || a.InstanceId != pet.InstanceId || !a.Pets.Contains(pet)) return;
            var targets = TowerPetTargets(pet, target, row, pet.AttackSkills.First(s => s.Id == skillId).Range);
            if (targets.Count == 0) return;
            var fire = SarahFireHit(row);
            var stats = pet.CombatStats;
            var attacker = new CombatService.Fighter(stats[AttributeId.Atk], stats[AttributeId.Matk], stats[AttributeId.Hit], stats[AttributeId.Cri], pet.Level, pet.Element);
            var hit = new SkillHitInfo
            {
                SrcID = pet.Uid, SkillId = skillId, ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow")),
                SessionId = castId, SrcElement = pet.Element,
            };
            var processed = new List<MonsterUnit>();
            var killed = new List<MonsterUnit>();
            try
            {
                foreach (var monster in targets)
                {
                    if (monster.Dead || monster.Hp <= 0) continue; // another cast may have reserved this actor while we awaited
                    processed.Add(monster);
                    var defender = MonsterFighter(monster);
                    var (damage, result) = Damage(attacker, defender, row);
                    if (fire is { } extra) damage += Damage(attacker, defender, extra).Damage;
                    foreach (var adjust in MonsterDamageAdjusters.Handlers) damage = adjust(monster, damage, false);
                    long previousHp = monster.Hp;
                    monster.Hp = Math.Max(0, monster.Hp - damage);
                    if (monster.Hp == 0) killed.Add(monster); // reserve before awaited callbacks
                    await SettleMonsterHitAsync(owner, monster, previousHp, damage);
                    hit.Dsts.Add(new DamageInfo
                    {
                        DstID = monster.Uid, Damage = damage, HitResult = result,
                        DstElement = monster.Template.Element, IsDead = monster.Hp == 0
                    });
                }
                foreach (var viewer in processed.SelectMany(m => m.Viewers).Append(owner).Distinct())
                    await viewer.SendGameAsync(SkillHit, hit);
                if (processed.Count > 0) await SurgeryHealAsync(pet, row);
                foreach (var monster in processed)
                {
                    if (a.Settled || owner.Dead) return;
                    if (monster.Hp == 0 && !monster.DeathDurable) continue; // another cast's uncommitted death
                    await _world.ToUnitViewersAsync(monster, AoiAttrChange, MonsterService.Hp(monster));
                    if (monster.Hp > 0)
                    {
                        _monsters.Engage(monster, pet);
                        await FightingAsync(monster);
                        continue;
                    }
                    if (!killed.Contains(monster)) continue; // a concurrent player/pet owns that lethal resolution
                    await _monsters.KillAsync(monster);
                    foreach (var other in _sessions.All().Where(o => o.Attack?.Target == monster))
                    {
                        other.Attack = null;
                        await other.SendGameAsync(SkillEnd, new SkillEndNotify
                        {
                            SrcID = other.Role!.Id, SessionId = castId, Type = SkillEndNotify.Types.SkillEndType.Interrupted,
                            InterruptedInfo = new SkillInterruptedInfo { Type = SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid },
                        });
                    }
                    await PublishDurableKillAsync(owner, monster, () => ReferenceEquals(owner.Role, castingRole)
                        && ReferenceEquals(owner.Attachment, castingAttachment) && !owner.SceneLoading
                        && owner.SceneGeneration == castingSceneGeneration && owner.WorldScene == monster.WorldScene);
                }
            }
            finally
            {
                foreach (var monster in killed.Where(m => m.Hp == 0 && m.DeathDurable))
                {
                    if (!monster.Dead)
                        try { await _monsters.KillAsync(monster); }
                        catch (Exception e) { _log.LogError(e, "Could not publish durable pet kill {Source}", monster.RewardSource?.Key); }
                    await PublishDurableKillAsync(owner, monster, () => ReferenceEquals(owner.Role, castingRole)
                        && ReferenceEquals(owner.Attachment, castingAttachment) && !owner.SceneLoading
                        && owner.SceneGeneration == castingSceneGeneration && owner.WorldScene == monster.WorldScene);
                    if (monster.Dead) await FinalizeLethalPublicationAsync(monster);
                }
            }
        });
    }

    /// <summary>
    /// The monsters a tower pet's attack hits: a Rect360 row the ones in its rectangle ahead of the pet, turned to its
    /// target (<see cref="SkillAreaContains"/>, _kRange [length, width]); another area row (_iRangeType 2) the ones within
    /// _kRange[0] of the pet; else the target. Nearest first, up to _iTargetMax.
    /// </summary>
    private List<MonsterUnit> TowerPetTargets(TowerPetUnit pet, MonsterUnit target, JsonElement row, float range)
    {
        var skill = new SkillConfig(row);
        float reach = (skill.Range.FirstOrDefault() is > 0 and var cm ? cm : 0) / 100f;
        int limit = (int)Math.Max(1, skill.TargetMax);
        bool rectangle = skill.Release == SkillRelease.Rect360;
        if (skill.RangeType != 2 || reach <= 0)
            return !target.Dead && target.Hp > 0 && target.InstanceId == pet.InstanceId
                && MonsterService.Distance(pet.X, pet.Z, target.X, target.Z) <= range + 2f ? [target] : [];
        float face = MonsterService.Angle(pet.X, pet.Z, target.X, target.Z);
        return [.. _world.Map(pet.WorldScene).Near(pet.X, pet.Z, reach + 1).OfType<MonsterUnit>()
            .Where(m => !m.Dead && m.Hp > 0 && m.InstanceId == pet.InstanceId && (rectangle
                ? SkillAreaContains(row, pet.Pos, target.Pos, face, m.Pos)
                : MonsterService.Distance(pet.X, pet.Z, m.X, m.Z) <= reach + 0.5f))
            .OrderBy(m => MonsterService.Distance(pet.X, pet.Z, m.X, m.Z)).Take(limit)];
    }

    /// <summary>
    /// Sarah's Blazing Thrust (2334401, script Pet_MmSarah_23344): "a ${5}% Chance to additionally deal @{1} hits of Fire
    /// ... totaling Pet Magic ATK*${6}%", once per cast. The extra hit is the row's second damage branch (_kDamageParam1[1],
    /// ${6} x 100) as Fire (4); the hit's damage type stays the row's (inferred). Null when it does not proc.
    /// </summary>
    private JsonElement? SarahFireHit(JsonElement row)
    {
        var skill = new SkillConfig(row);
        if (Script(row) != SarahScript || skill.DamageParam1.ElementAtOrDefault(1) <= 0) return null;
        int chance = int.TryParse(skill.DescData.ElementAtOrDefault(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0;
        return Rolls.Proc(100) < chance ? skill.AsDescribedHit(skill.DamageParam1[1], 0, FireElement) : null;
    }

    private const string SarahScript = "Pet_MmSarah_23344", SurgeryScript = "QunTiShouShu";
    private const long FireElement = 4;

    /// <summary>
    /// Aliot's AoE Surgery (2314401, script QunTiShouShu): "converting the damage coefficient * ${4}% into Healing for
    /// Aliot". The row's second damage value is that share of the first at every level (2314401 [18400, 3680] up to 2314406
    /// [36800, 7360]), so Aliot heals PATK x _kDamageParam1[1] / 10000 once per cast that hits (inferred).
    /// </summary>
    private async Task SurgeryHealAsync(TowerPetUnit pet, JsonElement row)
    {
        var skill = new SkillConfig(row);
        if (Script(row) != SurgeryScript || skill.DamageParam1.ElementAtOrDefault(1) <= 0 || pet.Dead || pet.Hp <= 0) return;
        long amount = (long)Math.Floor(pet.CombatStats[AttributeId.Atk] * skill.DamageParam1[1] / 10000.0);
        long actual = Math.Min(amount, Math.Max(0, pet.MaxHp - pet.Hp));
        if (actual <= 0) return;
        pet.Hp += actual;
        await _world.ToUnitViewersAsync(pet, AoiAttrChange, pet.HpChange());
    }

    private async Task MonsterAttackPetAsync(MonsterUnit monster, TowerPetUnit pet)
    {
        if (pet.Dead || pet.Hp <= 0 || monster.Dead || monster.Hp <= 0
            || pet.Owner.Instances.TowerAttempt is not { Loaded: true, Settled: false } attempt
            || !attempt.Pets.Contains(pet)) return;
        long castId = NextSessionId();
        var use = new UseInSkillInfo
        {
            SrcID = monster.Uid, SkillID = monster.Template.NormalSkill, TargetId = pet.Uid, TargetPos = pet.Pos,
            Face = monster.Face, SessionId = castId, SkillMs = monster.Template.SkillMs, StartTime = Now,
        };
        await _world.ToUnitViewersAsync(monster, UseInSkill, use);
        ScheduleRuntime(monster.WorldScene, TimeSpan.FromSeconds(monster.Template.HitPointSeconds), async () =>
        {
            if (pet.Dead || pet.Hp <= 0 || monster.Dead || monster.Hp <= 0 || monster.WorldScene != pet.WorldScene
                || pet.Owner.Instances.TowerAttempt is not { Loaded: true, Settled: false } current || !current.Pets.Contains(pet)
                || MonsterService.Distance(monster.X, monster.Z, pet.X, pet.Z) > monster.Template.AttackRange + 2f) return;
            await DamageTowerPetAsync(monster, pet, _data.Row("SkillConfig", monster.Template.NormalSkill), monster.Template.NormalSkill, castId, 1000002);
        });
    }

    /// <summary>A monster's hit on a tower pet: the row's damage, pet support resistance, shields, HP and its death.</summary>
    internal async Task DamageTowerPetAsync(MonsterUnit monster, TowerPetUnit pet, JsonElement? row, int skillId, long castId,
        int showPoint, int damagePercent = 100)
    {
        var attacker = MonsterFighter(monster);
        var stats = pet.CombatStats;
        var defender = new CombatService.Fighter(0, 0, 0, 0, pet.Level, pet.Element)
        { Def = stats[AttributeId.Def], Mdef = stats[AttributeId.Mdef], Flee = stats[AttributeId.Flee], ResCri = stats[AttributeId.ResCri] };
        var (damage, result) = row is { } r ? Damage(attacker, defender, r)
            : (Math.Max(1, monster.Template.Atk), DamageHitResult.Normal);
        damage = ScaleMonsterDamage(damage, monster.DamageRatio);
        damage = MonsterBuffDamage(monster, row, damage);
        if (damagePercent != 100) damage = damagePercent <= 0 ? 0 : ScaleMonsterDamage(damage, damagePercent * 100);
        foreach (var adjust in MonsterDamageAdjusters.Handlers) damage = adjust(monster, damage, true);
        damage = ApplyPetSupportResistance(damage, _buffs.UnitAttributes(pet), row, monster.Template.Element);
        long beforeShield = damage;
        damage = await _buffs.AbsorbAsync(pet, damage);
        pet.Hp = Math.Max(0, pet.Hp - damage);
        // Reserve death before any awaited broadcasts: neither the pet nor another pending swing
        // may act on zero HP while the death notification and substitution are being delivered.
        pet.Dead = pet.Hp == 0;
        var hit = new SkillHitInfo
        {
            SrcID = monster.Uid, SkillId = skillId, ShowPoint = showPoint,
            SessionId = castId, SrcElement = monster.Template.Element,
            Dsts = { new DamageInfo { DstID = pet.Uid, Damage = damage, SheildDamage = beforeShield - damage, HitResult = result,
                DstElement = pet.Element, IsDead = pet.Hp == 0 } },
        };
        await _world.ToUnitViewersAsync(monster, SkillHit, hit);
        await _world.ToUnitViewersAsync(pet, AoiAttrChange, pet.HpChange());
        if (!pet.Dead) return;
        await _buffs.ClearUnitAsync(pet);
        if (ReferenceEquals(monster.PetTarget, pet)) monster.PetTarget = null;
        await _world.ToUnitViewersAsync(pet, EntityBaseChange, pet.StateChange());
        await TowerPetDied.InvokeAsync(pet);
    }
}
