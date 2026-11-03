namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>AttributeConfig 207/208 and 1207/1208: independent healing done/received inputs, from gear, cards and buffs.
    /// (inferred) Multiply the two additive groups, floor once, then clamp to missing HP.</summary>
    internal static long ModifiedHealing(StatSheet source, StatSheet target, long amount) =>
        (long)Math.Clamp(Math.Floor(amount * Math.Max(0, 1 + (source[AttributeId.PerTreat] + source[AttributeId.PerTreat1]) / 10000.0)
            * Math.Max(0, 1 + (target[AttributeId.PerCure] + target[AttributeId.PerCure1]) / 10000.0)), 0, long.MaxValue);

    /// <summary>What a healer's heal reads: the sheet, without a totem's 207, which TotemEffects.HealingMultiplier already applied.</summary>
    private StatSheet HealerAttributes(GameSession healer)
    {
        var stats = _character.Stats(healer);
        if (!healer.Buffs.Any(b => b.IsTotemOwned)) return stats;
        double totem = healer.Buffs.Where(b => b.IsTotemOwned).SelectMany(b => b.Changes).Where(c => c.Attr == AttributeId.PerTreat).Sum(c => c.Value);
        return new StatSheet { [AttributeId.PerTreat] = stats[AttributeId.PerTreat] - (long)totem, [AttributeId.PerTreat1] = stats[AttributeId.PerTreat1] };
    }

    /// <summary>LC_PlayerInfoHelper._doFormulaHealsCriticalHits: CRI * FormulaParamHealCriA 0.5 / (CRI * 0.5 * FormulaParamHealCriB 0 + ParCri 10000),
    /// the row's _iSkillCri as on a hit. (inferred) Crit chance % adds to it as Formula_CureCriticalProbability, with no resistance: the target is a friend.</summary>
    internal static double HealCritRate(long cri, long critChance, long skillCri) =>
        Math.Max(0, cri * 0.5 + critChance) / 10000.0 * skillCri / 10000.0;

    /// <summary>LC_PlayerInfoHelper._doFormulaHealsCriticalDmg: FormulaParamCriDmg 1.5 + Crit Damage * FormulaParamHealCriC 0.5.</summary>
    internal static double HealCritMultiplier(long critDamage) => 1.5 + critDamage / 10000.0 * 0.5;

    /// <summary>AttributeConfig sp_dedution/per_sp_dedution (1231/1232); Marionette increases the latter.
    /// (inferred) Apply the flat then percent change once to the summed SP cost, rounding up.</summary>
    internal static long ModifiedSpCost(StatSheet stats, long amount) => amount <= 0 ? 0 :
        (long)Math.Clamp(Math.Ceiling(Math.Max(0, amount + stats[AttributeId.SpDedution]) * Math.Max(0, 1 + stats[AttributeId.PerSpDedution] / 10000.0)), 0, long.MaxValue);
}
