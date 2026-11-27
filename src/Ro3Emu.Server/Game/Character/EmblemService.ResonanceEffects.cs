using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a held Soul Echo resonance row (SkillInUse, op 6) does in combat. The rows carry only a description
/// (SkillConfig._iDescription with _kDescData); the client ships the skill forms they describe as mode rows of the
/// skills they name and the hidden BuffConfig rows that switch to them (_kSkillAttributeList type 15). Which switch
/// belongs to which resonance row is not linked in config: each pair below is matched by the mode row carrying the
/// description's numbers (inferred). The behaviours that are not a mode or a skill tuple are in ResonancePassives.
/// </summary>
public sealed partial class EmblemService
{
    // SkillConfig._iID of the resonance rows, named by SkillConfig._iName.
    internal const int PixelThrust = 11130501, ElectroRock = 11130701, GuardiansMight = 11164501,
        ElementalEnsemble = 11230501, MixedAccompaniment = 11230701, CoordinatedHunt = 11336401, RhythmicWarg = 11336601,
        Organum = 11431001, Baptism = 11431401, Encore = 11533001, FreestyleRap = 11533101, MelodyCannon = 11631201,
        MelodyVulcan = 11631401, AbsoluteDefense = 11164701;

    /// <summary>Resonance row -> the BuffConfig rows whose skill tuples apply while it is held.</summary>
    private static readonly Dictionary<int, int[]> ResonanceModeBuffs = new()
    {
        // "[Phantom Thrust]'s percentage damage multiplier increases by ${1}% ... within ${2} seconds of being HIT":
        // 1133501 sets 11308 to mode 1, whose rows add +15000 to _kDamageParam1 and put Target Mark 1133601 for 10000 ms.
        [PixelThrust] = [1133501],
        // "[Shield Block]'s DEF and MDEF bonuses increase by ${1}%": Shield Casting Passive 11164101 sets 11638 to mode 3, whose
        // block grants DMG Reduction 11165801 (+100% DEF/MDEF where 1164501 gives +50%) and Parry 11165501 (CombatService.ShieldBlock).
        [AbsoluteDefense] = [11164101],
        // "[Reflect Damage] range increases to ${1} meters, ... coefficient increases by ${4}%, and HIT applies Slow":
        // Switch Thorn 11163201 sets 11618 to mode 1 (_kRange 700, 1.5x _kDamageParam1, _kBuff2 slow 11163101).
        [GuardiansMight] = [11163201],
        // "Increases the skill multiplier of each hit of [Tetra Vortex] by ${1}%": Switch Vortex 1234201 sets 12316 and its
        // Reading copy 12326 to mode 1 (+5000 on each of the four hits).
        [MixedAccompaniment] = [1234201],
        // "... effect radius of [Shadow Guard], [Thousand Shadows], and [Circling Shadow] by ${1} m ...": Freestyle Rap
        // 11532701 adds one to the modes of 15320/15322/15323 (_kRange 700, +1500).
        [FreestyleRap] = [11532701],
        // "[Shadow Pierce] also grants you a stack of [Delusion]": Encore 11533001 adds one to 15326's mode.
        [Encore] = [11533001],
        // "Enhance [Cannon] into [Melody Cannon]": Season Cannon Switch BD 11631201 adds 2 to 16312's mode (ArmCannonS1BD1, and
        // with Firepower Loaded's +2 ArmCannonProS1BD2).
        [MelodyCannon] = [11631201],
        // "[Melody Vulcan] Damage Increase ${1}%": Season Vulcan Cannon Switch BD 11631401 sets 16313 to mode 2
        // (1.05x _kDamageParam1).
        [MelodyVulcan] = [11631401],
    };

    /// <summary>
    /// Resonance rows whose description is a skill tuple no BuffConfig row carries, written as one the way the talents'
    /// "percentage damage multiplier increases by ${n}%" are ([group, 29, add, n * 100], TalentProcs.Charges) (inferred).
    /// </summary>
    private static SkillParameterRules.Modifier[] DescribedModifiers(int row, string[] description) => row switch
    {
        // "The percentage damage multiplier of all forms of [Rain of Spears] is increased by ${1}%." Lance Knight: Fire
        // Mark 1134001 writes its own +25% on 11310 the same way ([11310, 29, 1, 2500]).
        ElectroRock => [new(RainOfSpears, 29, 1, (long)(DescribedValue(description, 0) * 100), -1)],
        // "Wolf Minions become Rhythmic Wargs": 13327/13328 mode 1 rows are named "Rhythmic Warg Minion".
        RhythmicWarg => [new(WargCharge, 15, 1, 1, 0), new(WargBite, 15, 1, 1, 0)],
        _ => [],
    };

    private const int RainOfSpears = 11310, WargCharge = 13327, WargBite = 13328;

    /// <summary>After the held resonance rows or their skill tuples change: SkillCastingService republishes 31132/31027.</summary>
    public AsyncHook<GameSession> EffectsChanged { get; } = new("EmblemService.EffectsChanged");

    /// <summary>
    /// Recompute the session's held resonance rows and their skill tuples after a Soul Echo, plan, Build or job change.
    /// The scene load publishes 31132 itself.
    /// </summary>
    internal async Task RefreshEffectsAsync(GameSession session)
    {
        int[] held = [.. ResonanceSkills(session).Order()];
        if (held.AsSpan().SequenceEqual(session.ResonanceSkills)) return;
        session.ResonanceSkills = held;
        session.ResonanceSkillModifiers = [.. held.SelectMany(ResonanceModifiers)];
        if (session.SceneLoading || session.Role == null) return;
        await EffectsChanged.InvokeAsync(session);
    }

    private IEnumerable<SkillParameterRules.Modifier> ResonanceModifiers(int row)
    {
        if (ResonanceModeBuffs.TryGetValue(row, out var buffs))
            foreach (int buff in buffs)
                foreach (var modifier in SkillParameterRules.BuffModifiers(_data, buff)) yield return modifier;
        foreach (var modifier in DescribedModifiers(row, SkillConfig.Get(_data, row)?.DescData ?? [])) yield return modifier;
    }

    /// <summary>The row's _kDescData value, read as a number (0 when absent).</summary>
    internal static double DescribedValue(string[] description, int index) =>
        index < description.Length && double.TryParse(description[index], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
}
