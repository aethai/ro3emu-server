using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    private const int ControlBuffTag = 37, NegativeBuffType = 2;
    private const int ControlChargeGroup = 9101; // Chimera's three _kBuff1 entries; three applications, capped by BuffConfig._iMaxStack.

    // SpecialStatusConfig 22-24 (_kDesc): immunity is implemented through the holder buff's _kBuffCategory. Super Armor
    // 9031 carries 19, control immunity 9032 (and the MVP born buff 9009) 20, debuff immunity 9033 21, 9034 22.
    private const int DisplacementImmunityCategory = 19, ControlImmunityCategory = 20,
        DebuffImmunityCategory = 21, AllNegativeImmunityCategory = 22;

    /// <summary>Berserk 1121401: "Gain Slow Immunity"; Slows carry category 33, and its buff holds no immunity category of its own.</summary>
    private const int SlowCategory = 33, BerserkGroup = 11214;

    /// <summary>Card: Eddga 2052301: "Grants permanent [Endure] (immune to knockback, knock-up, and hitstun effects)." Its row holds nothing, and carries
    /// none of the category 19 the Endure rows do. (inferred) It counts as one. The hitstun is the client's own flinch: nothing on the server to refuse.</summary>
    private const int EddgaGroup = 20523;

    /// <summary>Card: Moonlight Flower 2052501 "Immune to Slow", Card: Orc Hero 2052701 "Immune to Stun". The rows carry only that text; Stuns carry
    /// category 13. (inferred) A row with the category is refused whole, as for Berserk, even one that does more than slow.</summary>
    private const int StunCategory = 13, MoonlightFlowerGroup = 20525, OrcHeroGroup = 20527;

    /// <summary>Praefatio 1430201: "grants them Immunity to up to ${3} [Status Effect] and [Movement Restriction] effects".</summary>
    private const int PraefatioGroup = 14302;
    private const long PraefatioSkill = 1430200;
    private readonly Dictionary<ulong, int> _immunityCharges = [];

    /// <summary>One Praefatio charge for an effect it refused; the last ends it.</summary>
    private async Task SpendImmunityChargeAsync(GameSession player)
    {
        if (player.Buffs.FirstOrDefault(b => b.ExpireMs > MonsterService.NowMs && b.ConfigId / 100 == PraefatioGroup) is not { } buff) return;
        int left = _immunityCharges.TryGetValue((ulong)buff.Guid, out int held) ? held
            : Data.Config.SkillConfig.Get(_data, PraefatioSkill + Math.Max(1, buff.ConfigId % 100)) is { DescData: [_, _, var count, ..] }
                && int.TryParse(count, out int charges) ? charges : 1;
        if (--left > 0) { _immunityCharges[(ulong)buff.Guid] = left; return; }
        _immunityCharges.Remove((ulong)buff.Guid);
        await RemoveAsync(player, buff);
    }

    private async Task<bool> RejectIncomingAsync(GameSession player, JsonElement incoming, bool guardianOwned, long sourceId)
    {
        if (ImmuneTo(player.Buffs, incoming))
        {
            await SpendImmunityChargeAsync(player);
            return true;
        }
        if (GameData.Int(incoming, "_iType") != NegativeBuffType
            || !GuardianPetRules.Numbers(incoming, "_kBuffCategory").Contains(ControlBuffTag)) return false;
        var charge = player.Buffs.FirstOrDefault(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == ControlChargeGroup);
        // A control that diminishing returns refuses anyway costs no charge.
        if (charge == null || sourceId != player.Role!.Id
            && DecayRefuses(player.Role.Id, player.SceneEntry, (int)GameData.Int(incoming, "_iID"))) return false;
        // (inferred) Spend one charge per incoming control application; ordinary immunity spends none.
        if (charge.PileCount <= 1) await RemoveAsync(player, charge);
        else await AddCoreAsync(player, charge.ConfigId, (int)Math.Clamp(charge.ExpireMs - MonsterService.NowMs, 1, int.MaxValue),
            charge.SourceId, charge.PileCount - 1, guardianOwned: true);
        return true;
    }

    /// <summary>Tag 37 identifies control effects and _iType 2 debuffs; the holder's categories decide immunity, whoever gave either.
    /// (inferred) Reject the incoming buff before decay, rather than merely hiding its icon.</summary>
    private bool ImmuneTo(IEnumerable<ActiveBuff> active, JsonElement incoming)
    {
        if (GameData.Int(incoming, "_iType") != NegativeBuffType) return false;
        long now = MonsterService.NowMs;
        if (active.Any(b => b.ExpireMs > now && b.ShieldDebuffImmunity && b.ShieldRemaining > 0)) return true;
        var categories = GuardianPetRules.Numbers(incoming, "_kBuffCategory");
        bool control = categories.Contains(ControlBuffTag);
        bool displacement = GameData.Int(incoming, "_iBuffID") is KnockBackGroup or KnockUpGroup;
        // Chimera's charge also carries control immunity (category 20), but it is spent per application instead.
        bool slow = categories.Contains(SlowCategory), stun = categories.Contains(StunCategory);
        return active.Any(b => b.ExpireMs > now
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") != ControlChargeGroup
            && GuardianPetRules.Numbers(row, "_kBuffCategory") is var held
            && (held.Contains(DebuffImmunityCategory) || held.Contains(AllNegativeImmunityCategory)
                || control && held.Contains(ControlImmunityCategory)
                || displacement && (held.Contains(DisplacementImmunityCategory) || b.ConfigId / 100 == EddgaGroup)
                || slow && b.ConfigId / 100 is BerserkGroup or MoonlightFlowerGroup
                || stun && b.ConfigId / 100 == OrcHeroGroup));
    }

    /// <summary>SpecialStatusConfig 22 Endure "免疫击退、击飞、僵直" and 23 control immunity, whose text lists knockback and launch too: nothing moves a holder.</summary>
    private bool HoldsDisplacementImmunity(IEnumerable<ActiveBuff> active) => active.Any(b => b.ExpireMs > MonsterService.NowMs
        && (b.ConfigId / 100 == EddgaGroup || BuffConfig.Get(_data, b.ConfigId) is { } row && row.BuffCategory.Any(c => c is DisplacementImmunityCategory or ControlImmunityCategory or AllNegativeImmunityCategory)));

    internal bool ResistsDisplacement(GameSession player) => HoldsDisplacementImmunity(player.Buffs);

    internal bool ResistsDisplacement(WorldUnit unit) => _unitBuffs.TryGetValue(unit.Uid, out var buffs) && HoldsDisplacementImmunity(buffs);

    /// <summary>A unit holding a control immunity (category 20: the MVPs' born buffs 900801/900901, 9032) resists a control
    /// (tag 37) from any source: Element Control's "When a Boss target resists your [Frozen], [Petrify], or [Stun]" says
    /// bosses resist them. (inferred) Units only; a player's immunity keeps the guardian-only scope of ImmuneTo.</summary>
    private bool UnitResistsControl(IEnumerable<ActiveBuff> active, JsonElement incoming)
    {
        var control = new BuffConfig(incoming);
        if (control.Type != NegativeBuffType || !control.BuffCategory.Contains(ControlBuffTag)) return false;
        long now = MonsterService.NowMs;
        return active.Any(b => b.ExpireMs > now && BuffConfig.Get(_data, b.ConfigId) is { } row
            && row.BuffId != ControlChargeGroup && row.BuffCategory.Contains(ControlImmunityCategory));
    }

    /// <summary>A unit resisted a control a player applied: the unit, the player's role id and the control's BuffConfig
    /// row (PassiveSkills: Element Control).</summary>
    internal List<Func<WorldUnit, long, JsonElement, Task>> ControlResisted { get; } = [];

    internal async Task DispelControlsAsync(GameSession player, bool allDebuffs = false)
    {
        foreach (var buff in player.Buffs.Where(b => !b.IsPassive && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iType") == NegativeBuffType && GameData.Int(row, "_iDisperse") != 0
            && (allDebuffs || GuardianPetRules.Numbers(row, "_kBuffCategory").Contains(ControlBuffTag))).ToArray())
            await RemoveAsync(player, buff);
    }

    internal bool UnitForbidden(WorldUnit unit, int ordinal) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal > 0
            && _data.Row("SpecialStatusConfig", b.StateOrdinal) is { } state
            && GuardianPetRules.Numbers(state, "_kForbidden").Contains(ordinal));

    /// <summary>Does the unit carry a live negative buff (BuffConfig._iType 2: Stun, Immobilize, Poison ...).</summary>
    internal bool UnitHasDebuff(WorldUnit unit) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && BuffConfig.Get(_data, b.ConfigId)?.Type == NegativeBuffType);

    internal bool UnitHasGroup(WorldUnit unit, int group) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.ExpireMs > MonsterService.NowMs && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") == group);

    internal ulong UnitStatus(WorldUnit unit) => !_unitBuffs.TryGetValue(unit.Uid, out var buffs) ? 0
        : buffs.Where(b => b.ExpireMs > MonsterService.NowMs && b.StateOrdinal is > 0 and < 33)
            .Aggregate(0UL, (mask, b) => mask | 1UL << (b.StateOrdinal - 1));

    internal ulong UnitForbiddenMask(WorldUnit unit)
    {
        ulong mask = 0;
        for (int ordinal = 1; ordinal < 32; ordinal++)
            if (UnitForbidden(unit, ordinal)) mask |= ForbiddenMask(ordinal);
        return mask;
    }
}
