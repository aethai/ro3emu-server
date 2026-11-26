using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Pure-rule unit tests on invented synthetic JSON tables so CI exercises stat calculation, combat restraints,
/// pet combat scaling, tower suppression, and buff rule tables without exported client data.
/// </summary>
public sealed class PureRulesSyntheticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-purerules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    private GameData BuildData(IReadOnlyList<(string Table, string Json)> tables)
    {
        string configDir = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "config");
        Directory.CreateDirectory(configDir);
        foreach (var (table, json) in tables)
            File.WriteAllText(Path.Combine(configDir, table + ".json"), json);
        string root = Path.GetDirectoryName(configDir)!;
        return new GameData(Options.Create(new ServerOptions { GameDataDir = root }), NullLogger<GameData>.Instance);
    }

    private static RoleProgress Role(int career) => new() { Career = career, Level = 99 };

    private static RoleProgress Role(int career, int s1)
    {
        var progress = Role(career);
        progress.Skills[s1 / 100] = s1 % 100;
        return progress;
    }

    private static RoleProgress Role(int career, int s1, int s2)
    {
        var progress = Role(career, s1);
        progress.Skills[s2 / 100] = s2 % 100;
        return progress;
    }

    private static RoleProgress Role(int career, int s1, int s2, int s3)
    {
        var progress = Role(career, s1, s2);
        progress.Skills[s3 / 100] = s3 % 100;
        return progress;
    }

    // ---- StatCalculator ----

    [Fact]
    public void StatCalculatorAppliesFullGroupFormulaAndCeilingRounding()
    {
        var data = BuildData(
        [
            ("AttributeConfig", """
            {
              "103":  { "_iID": 103,  "_iPreAttri": 103, "_iCalculateType": 1, "_iDataType": 0 },
              "1031": { "_iID": 1031, "_iPreAttri": 103, "_iCalculateType": 2, "_iDataType": 1 },
              "1032": { "_iID": 1032, "_iPreAttri": 103, "_iCalculateType": 3, "_iDataType": 0 },
              "1033": { "_iID": 1033, "_iPreAttri": 103, "_iCalculateType": 4, "_iDataType": 1 },
              "1034": { "_iID": 1034, "_iPreAttri": 103, "_iCalculateType": 6, "_iDataType": 1 }
            }
            """),
        ]);

        // B1=100, B2=0.10 (1000/10000), B6=0.05 (500/10000), B4=0.20 (2000/10000), B3=7
        // final = 100 * (1 + 0.15) * (1 + 0.20) + 7 = 138 + 7 = 145
        var sheet = StatCalculator.Compute(new Dictionary<int, double>
        {
            [103] = 100,
            [1031] = 1000,
            [1032] = 7,
            [1033] = 2000,
            [1034] = 500,
        }, data);

        Assert.Equal(145, sheet[103]);
        Assert.Equal(100, sheet.Base[103]);
        Assert.Equal(1000, sheet[1031]);
        Assert.Equal(7, sheet[1032]);
        Assert.Equal(2000, sheet[1033]);
        Assert.Equal(500, sheet[1034]);

        // Fractional integer result rounds up: 22 * 1.01 = 22.22 -> 23.
        var ceiled = StatCalculator.Compute(new Dictionary<int, double> { [103] = 22, [1031] = 100 }, data);
        Assert.Equal(23, ceiled[103]);
        Assert.Equal(22, ceiled.Base[103]);
    }

    [Fact]
    public void StatCalculatorScalesPercentAndAspdDataTypesAndPassesUngroupedValues()
    {
        var data = BuildData(
        [
            ("AttributeConfig", """
            {
              "115":  { "_iID": 115,  "_iPreAttri": 115, "_iCalculateType": 1, "_iDataType": 2 },
              "114":  { "_iID": 114,  "_iPreAttri": 115, "_iCalculateType": 2, "_iDataType": 2 },
              "1142": { "_iID": 1142, "_iPreAttri": 115, "_iCalculateType": 3, "_iDataType": 2 },
              "117":  { "_iID": 117,  "_iPreAttri": 0,   "_iCalculateType": 1, "_iDataType": 0 }
            }
            """),
        ]);

        var sheet = StatCalculator.Compute(new Dictionary<int, double>
        {
            [115] = 100,  // 1.00
            [114] = 20,   // +20%
            [1142] = 10,  // +0.10
            [117] = 42.6, // ungrouped: Data rounded, Base floored
        }, data);

        Assert.Equal(130, sheet[115]);
        Assert.Equal(43, sheet[117]);
        Assert.Equal(42, sheet.Base[117]);

        var clone = sheet.Clone();
        clone[115] = 999;
        Assert.Equal(130, sheet[115]);
    }

    // ---- JobResonance, TotemRestraint, PlayerRestraints, TitleDamage ----

    [Fact]
    public void JobResonanceSelectsFirstMetRowAndEvaluatesCountersAttributesAndPower()
    {
        var data = BuildData(
        [
            ("JobResonanceActiveConfig", """
            {
              "1": { "_iId": 1, "_iJob": 1400, "_iJobResonanceId": 2, "_iJobResonanceSkills": [10105, 10203] },
              "2": { "_iId": 2, "_iJob": 1400, "_iJobResonanceId": 3, "_iJobResonanceSkills": [10302] },
              "3": { "_iId": 3, "_iJob": 4400, "_iJobResonanceId": 5, "_iJobResonanceSkills": [40101] }
            }
            """),
            ("JobResonanceRestraintConfig", """
            {
              "1": { "_iID": 1, "_iJobProfess1": 10000, "_iJobProfess2": 9000,  "_iJobProfess3": 9000,  "_iJobProfess5": 9000 },
              "2": { "_iID": 2, "_iJobProfess1": 11000, "_iJobProfess2": 10000, "_iJobProfess3": 11000, "_iJobProfess5": 10000 },
              "3": { "_iID": 3, "_iJobProfess1": 11000, "_iJobProfess2": 9000,  "_iJobProfess3": 10000, "_iJobProfess5": 10000 },
              "5": { "_iID": 5, "_iJobProfess1": 11000, "_iJobProfess2": 10000, "_iJobProfess3": 10000, "_iJobProfess5": 10000 }
            }
            """),
            ("JobResonanceConfig", """
            {
              "1": { "_iID": 1, "_iJobResonanceId": 1, "_iPower": 0,   "_kAttribute": [] },
              "2": { "_iID": 2, "_iJobResonanceId": 2, "_iPower": 500, "_kAttribute": [] },
              "5": { "_iID": 5, "_iJobResonanceId": 5, "_iPower": 500, "_kAttribute": [[207, 500]] }
            }
            """),
        ]);

        var resonance = new JobResonance(data);
        Assert.Equal(2, resonance.ResonanceId(Role(1400, 10105, 10203)));
        Assert.Equal(3, resonance.ResonanceId(Role(1400, 10302)));
        Assert.Equal(2, resonance.ResonanceId(Role(1400, 10105, 10203, 10302))); // lower _iId wins
        Assert.Equal(JobResonance.Inactive, resonance.ResonanceId(Role(1400, 10105, 10202)));
        Assert.Equal(JobResonance.Inactive, resonance.ResonanceId(Role(1000)));

        Assert.Equal(11000, resonance.Multiplier(2, 3));
        Assert.Equal(9000, resonance.Multiplier(3, 2));
        Assert.Equal(9000, resonance.Multiplier(1, 5));
        Assert.Equal(10000, resonance.Multiplier(99, 2)); // unlisted defaults neutral

        var gospelAttr = Assert.Single(resonance.Attributes(5));
        Assert.Equal((207L, 500d), gospelAttr);
        Assert.Empty(resonance.Attributes(2));
        Assert.Equal(500, resonance.Power(2));
        Assert.Equal(0, resonance.Power(JobResonance.Inactive));
    }

    [Fact]
    public void TotemRestraintAndPlayerRestraintsStackCountersAndPreserveZeroDamage()
    {
        var data = BuildData(
        [
            ("JobConfig", """
            {
              "1400": { "_iID": 1400, "_iTotem": 1 },
              "4400": { "_iID": 4400, "_iTotem": 4 },
              "3400": { "_iID": 3400, "_iTotem": 3 },
              "5400": { "_iID": 5400, "_iTotem": 2 }
            }
            """),
            ("TotemRestraintConfig", """
            {
              "1": { "_iID": 1, "_iTotem0": 0,    "_iTotem1": 0,    "_iTotem2": 0,    "_iTotem3": 2000 },
              "2": { "_iID": 2, "_iTotem0": 2000, "_iTotem1": 0,    "_iTotem2": 0,    "_iTotem3": 0 },
              "3": { "_iID": 3, "_iTotem0": 0,    "_iTotem1": 2000, "_iTotem2": 0,    "_iTotem3": 0 },
              "4": { "_iID": 4, "_iTotem0": 0,    "_iTotem1": 0,    "_iTotem2": 2000, "_iTotem3": 0 }
            }
            """),
            ("JobResonanceActiveConfig", """
            {
              "1": { "_iId": 1, "_iJob": 1400, "_iJobResonanceId": 2, "_iJobResonanceSkills": [10101] },
              "2": { "_iId": 2, "_iJob": 4400, "_iJobResonanceId": 3, "_iJobResonanceSkills": [40101] }
            }
            """),
            ("JobResonanceRestraintConfig", """
            {
              "2": { "_iID": 2, "_iJobProfess1": 11000, "_iJobProfess2": 10000, "_iJobProfess3": 11000 },
              "3": { "_iID": 3, "_iJobProfess1": 11000, "_iJobProfess2": 9000,  "_iJobProfess3": 10000 }
            }
            """),
        ]);

        var resonance = new JobResonance(data);
        var totems = new TotemRestraint(data);
        Assert.Equal(2000, totems.Bonus(1400, 4400));
        Assert.Equal(2000, totems.Bonus(4400, 3400));
        Assert.Equal(2000, totems.Bonus(3400, 5400));
        Assert.Equal(2000, totems.Bonus(5400, 1400));
        Assert.Equal(0, totems.Bonus(4400, 1400));
        Assert.Equal(0, totems.Bonus(1400, 0));

        var attacker = Role(1400, 10101); // resonance 2, totem 1
        var target = Role(4400, 40101);   // resonance 3, totem 4
        // 1000 * 1.10 (resonance) * 1.20 (totem) = 1320
        Assert.Equal(1320, PlayerRestraints.Apply(resonance, totems, attacker, target, 1000));
        Assert.Equal(900, PlayerRestraints.Apply(resonance, totems, target, attacker, 1000));
        Assert.Equal(0, PlayerRestraints.Apply(resonance, totems, attacker, target, 0));
        Assert.Equal(1, PlayerRestraints.Apply(resonance, totems, target, attacker, 1));
    }

    [Fact]
    public void TitleDamageComputesTierDifferenceAndFloorsPositiveHitsAtOne()
    {
        var data = BuildData(
        [
            ("TitleConfig", """
            {
              "1":  { "_iID": 1,  "_kIndex": 1,  "_kTitleLev": 1, "_iPower": 0, "_iPowerAdd": 0, "_kAttr": [] },
              "2":  { "_iID": 2,  "_kIndex": 2,  "_kTitleLev": 1, "_iPower": 0, "_iPowerAdd": 0, "_kAttr": [] },
              "10": { "_iID": 10, "_kIndex": 10, "_kTitleLev": 5, "_iPower": 0, "_iPowerAdd": 0, "_kAttr": [] }
            }
            """),
            ("TitleBuffConfig", """
            {
              "1": { "_iID": 1, "_kIndex": 1, "_kTitleName": 400 },
              "5": { "_iID": 5, "_kIndex": 5, "_kTitleName": 2000 }
            }
            """),
        ]);

        var titles = new TitleDamage(data);
        Assert.Equal((0L, 400L, 2000L), (titles.Term(0), titles.Term(1), titles.Term(10)));
        Assert.Equal(1160, titles.Apply(10, 1, 1000));
        Assert.Equal(840, titles.Apply(1, 10, 1000));
        Assert.Equal(1000, titles.Apply(1, 2, 1000));
        Assert.Equal(1040, titles.Apply(1, 0, 1000));
        Assert.Equal(0, titles.Apply(10, 1, 0));
        Assert.Equal(1, titles.Apply(1, 10, 1));
    }

    // ---- PetCombatRules ----

    [Fact]
    public void PetCombatRulesOverlaysBattleAttributesScalesByQualityAndHonorsUnscaledList()
    {
        var data = BuildData(
        [
            ("PublicParamConfig", """{ "PetNotImproveBaseAttr": [102] }"""),
            ("PetBaseConfig", """{ "30001": { "_iID": 30001, "_iMonster": 5001, "_iQuality": 5 } }"""),
            ("NPCConfig", """{ "5001": { "_iID": 5001, "_iAttributeVariated": 10, "_iBattleAttributeVariated": 20, "_iSpeed": 420 } }"""),
            ("AttriMonsterConfig", """
            {
              "10001": { "_iID": 10001, "_kAttri": [[101, 1000], [102, 500], [103, 200], [105, 50]] },
              "20001": { "_iID": 20001, "_kAttri": [[101, 2000], [103, 300]] }
            }
            """),
            ("PetStarConfig", """{ "1": { "_iID": 1, "_iPetID": 30001, "_iStar": 1, "_iStage": 0, "_iBaseAttr": 15000 } }"""),
            ("PetAttrChangeConfig", """{ "1": { "_iID": 1, "_iQuality": 5, "_iStar": 1, "_iStage": 0, "_kAttrPerc": [[101, 22500], [103, 11000]] } }"""),
        ]);

        // Level 0 maps to 1; battle attrs override 101 & 103; 102 is unscaled; 105 uses _iBaseAttr (15000/10000).
        var attrs = PetCombatRules.Attributes(data, 30001, new PetData { PetId = 30001, Level = 0, Star = 1, StarProgress = 0 });
        Assert.NotNull(attrs);
        Assert.Equal(4500.0, attrs[101], 6); // 2000 * 2.25
        Assert.Equal(500.0, attrs[102], 6);  // unscaled
        Assert.Equal(330.0, attrs[103], 6);  // 300 * 1.10
        Assert.Equal(75.0, attrs[105], 6);   // 50 * 1.50
        Assert.Equal(2.0, attrs[109], 6);    // HIT = level + 1
        Assert.Equal(1.0, attrs[110], 6);    // FLEE = level
        Assert.Equal(100.0, attrs[115], 6);  // neutral ASPD
        Assert.Equal(420.0, attrs[113], 6);  // NPC speed
    }

    [Fact]
    public void PetCombatRulesClassifiesSupportScriptsAndComputesAttributeAmounts()
    {
        static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
        static JsonElement Skill(string script) => JsonSerializer.SerializeToElement(new { _kScriptLogic = script });

        Assert.Equal(new PetCombatRules.Support(PetCombatRules.SupportKind.Buff), PetCombatRules.SupportFor(Skill("BoLiPaoPaoTower")));
        Assert.Equal(new PetCombatRules.Support(PetCombatRules.SupportKind.Buff, SelfOnly: true), PetCombatRules.SupportFor(Skill("HarvestSickle")));
        Assert.Equal(new PetCombatRules.Support(PetCombatRules.SupportKind.AttributeHeal), PetCombatRules.SupportFor(Skill("YaoGunPT")));
        Assert.Equal(new PetCombatRules.Support(PetCombatRules.SupportKind.MagicHeal), PetCombatRules.SupportFor(Skill("Alice_Huzhu")));
        Assert.Null(PetCombatRules.SupportFor(Skill("UnknownScript")));

        var caster = new StatSheet();
        caster[101] = 3363;
        var recipient = new StatSheet();
        recipient[101] = 5000;

        var casterRule = Parse("""{ "_kDamageFormulaExtra": ["T_AXadd0_etc"], "_kDamageParamExtra": [[1, 650, 101, 3]] }""");
        var recipientRule = Parse("""{ "_kDamageFormulaExtra": ["T_AXadd0_etc"], "_kDamageParamExtra": [[2, 1000, 101, 3]] }""");
        var invalidRule = Parse("""{ "_kDamageFormulaExtra": ["Other"], "_kDamageParamExtra": [[1, 650, 101, 3]] }""");

        Assert.Equal(218, PetCombatRules.AttributeAmount(casterRule, caster, recipient));
        Assert.Equal(500, PetCombatRules.AttributeAmount(recipientRule, caster, recipient));
        Assert.Equal(0, PetCombatRules.AttributeAmount(invalidRule, caster, recipient));
    }

    // ---- TowerService.Suppress ----

    [Fact]
    public void TowerSuppressSelectsHighestMetPowerPercentThresholdAndDifficultyRatio()
    {
        var data = BuildData(
        [
            ("PowerSuppressConfig", """{ "1": { "_iID": 1, "_kApplicableLevel": [0, 80, 100, 110], "_kApplicableBuff": [9001, 9002, 0, 9003] } }"""),
            ("BuffConfig", """
            {
              "9001": { "_iID": 9001, "_kChangeAttri": [[215, -1000], [216, -1000]] },
              "9002": { "_iID": 9002, "_kChangeAttri": [[215, -500],  [216, -500]] },
              "9003": { "_iID": 9003, "_kChangeAttri": [[215, 1000],  [216, 1000]] }
            }
            """),
        ]);

        var floor = JsonDocument.Parse("""{ "_iPowerSuppress": 1, "_iPowerRecommend": 6000 }""").RootElement.Clone();
        Assert.Equal(new TowerService.Suppression(9001, -1000, -1000), TowerService.Suppress(data, floor, 4799, 10000));
        Assert.Equal(new TowerService.Suppression(9002, -500, -500), TowerService.Suppress(data, floor, 4800, 10000));
        Assert.Equal(new TowerService.Suppression(0, 0, 0), TowerService.Suppress(data, floor, 6000, 10000));
        Assert.Equal(new TowerService.Suppression(9003, 1000, 1000), TowerService.Suppress(data, floor, 6600, 10000));
        Assert.Equal(new TowerService.Suppression(0, 0, 0), TowerService.Suppress(data, floor, 5400, 9000)); // 90% recommendation
    }

    // ---- BuffRuleTables ----

    [Fact]
    public void BuffRuleTablesComputesTagFactorsAndControlDecayImmunity()
    {
        var data = BuildData(
        [
            ("BuffTagConfig", """
            {
              "1001": { "_iID": 1001, "_iFormula": 1, "_iAtkAttrID": 701, "_iDefAttrID": 702 },
              "1002": { "_iID": 1002, "_iFormula": 2, "_iAtkAttrID": 703, "_iDefAttrID": 704 },
              "1007": { "_iID": 1007, "_iFormula": 0, "_iAtkAttrID": 0,   "_iDefAttrID": 0 }
            }
            """),
            ("SpecialStatusDecay", """
            {
              "1": { "_iID": 1, "_iDecayType": 10000, "_iDecayRatio": 5000, "_iImmunityTime": 10000, "_iImmunodetection": 3, "_iIntervalDetection": 15000, "_kBuffCategory": [1001] }
            }
            """),
            ("StateExclusionConfig", """
            {
              "1": { "_iID": 1, "_iSpecialStatusID": 13 },
              "2": { "_iID": 2, "_iSpecialStatusID": 14 }
            }
            """),
            ("DungeonConfig", """
            {
              "2001": { "_iID": 2001, "_iDecayType": 20000 },
              "2002": { "_iID": 2002, "_iDecayType": 0 }
            }
            """),
        ]);

        var rules = BuffRuleTables.For(data);
        Assert.Equal(2, rules.Tags.Count);
        Assert.Equal(2, rules.ExclusiveStates.Count);
        Assert.Equal(13, rules.ExclusiveStates[0]);
        Assert.Equal(14, rules.ExclusiveStates[1]);
        Assert.Equal(20000, rules.DecayTypeOf(2001));
        Assert.Equal(BuffRuleTables.DefaultDecayType, rules.DecayTypeOf(2002));
        Assert.Equal(BuffRuleTables.DefaultDecayType, rules.DecayTypeOf(9999));

        // Formula 1 and 2 are both neutral at (0, 0) and give ~0.7042 at (0, 420).
        Assert.Equal(1.0, rules.TagFactor(0, 0, 1), 6);
        Assert.Equal(1.0, rules.TagFactor(0, 0, 2), 6);
        Assert.Equal(1.0 / 1.42, rules.TagFactor(0, 420, 1), 6);
        Assert.Equal(1.0 - 420.0 / 1420.0, rules.TagFactor(0, 420, 2), 6);
        // Extreme negative defense clamps cleanly without dividing by zero.
        Assert.True(double.IsFinite(rules.TagFactor(-9900, -9900, 1)));
        Assert.True(double.IsFinite(rules.TagFactor(-9900, -9900, 2)));

        // Diminishing returns: 1st = 100%, 2nd = 50%, 3rd = refused + 10 s immunity, resets after immunity or quiet window.
        var decay = Assert.Single(rules.Decays);
        var track = new BuffRuleTables.DecayTrack();
        Assert.False(BuffRuleTables.WouldRefuse(decay, track, 1000));
        Assert.True(BuffRuleTables.Land(decay, track, 1000, out double f1));
        Assert.Equal(1.0, f1, 6);

        Assert.False(BuffRuleTables.WouldRefuse(decay, track, 4000));
        Assert.True(BuffRuleTables.Land(decay, track, 4000, out double f2));
        Assert.Equal(0.5, f2, 6);

        Assert.True(BuffRuleTables.WouldRefuse(decay, track, 6000));
        Assert.False(BuffRuleTables.Land(decay, track, 6000, out _));
        Assert.True(BuffRuleTables.WouldRefuse(decay, track, 15999));
        Assert.False(BuffRuleTables.Land(decay, track, 15999, out _));

        // After immunity ends (6000 + 10000 = 16000), control lands at full duration again.
        Assert.False(BuffRuleTables.WouldRefuse(decay, track, 16000));
        Assert.True(BuffRuleTables.Land(decay, track, 16000, out double afterImmunity));
        Assert.Equal(1.0, afterImmunity, 6);

        // After a quiet window (> 15000 ms), count resets to 1.
        Assert.True(BuffRuleTables.Land(decay, track, 32000, out double afterQuiet));
        Assert.Equal(1.0, afterQuiet, 6);
    }

    [Fact]
    public void BuffRuleTablesParsesHalosAndPeriodicDamageAndHealRules()
    {
        var data = BuildData(
        [
            ("Localization_en", """
            {
              "90001": { "_iID": 90001, "_kDes": "Every @{1} sec, loses HP." },
              "90002": { "_iID": 90002, "_kDes": "Takes damage periodically." }
            }
            """),
            ("BuffConfig", """
            {
              "5001": { "_iID": 5001, "_iBuffID": 500, "_iType": 1, "_kLogicData": [5001, 5002] },
              "5002": { "_iID": 5002, "_iBuffID": 502, "_iType": 1 },
              "6001": { "_iID": 6001, "_iBuffID": 600, "_iType": 1, "_kDamageFormulaExtra": ["T_AXaddBadd3_etc"], "_kDamageParamExtra": [[0, 0, 101, 350]] },
              "6002": { "_iID": 6002, "_iBuffID": 601, "_iType": 2, "_iDmgType": 2, "_iEleType": 3, "_kData1": [6000], "_kDamageFormulaExtra": ["T_Xplus1_Xplus2_Xisbuff"], "_kDamageParamExtra": [[2, 1, 10]] },
              "6003": { "_iID": 6003, "_iBuffID": 602, "_iType": 2, "_iDmgType": 1, "_iEleType": 1, "_kData1": [2500], "_iDescription": [90001, 0.5] },
              "6004": { "_iID": 6004, "_iBuffID": 603, "_iType": 2, "_iDmgType": 2, "_iEleType": 2, "_kData1": [3000], "_iDescription": [90002] }
            }
            """),
            ("HaloConfig", """
            {
              "5001": { "_iID": 5001, "_iRadius": 800, "_iTickTime": 500, "_iTargetMax": 5, "_iTargetType": 1, "_iExtraTarget": [1] }
            }
            """),
        ]);

        var rules = BuffRuleTables.For(data);
        var halo = Assert.Single(rules.Halos).Value;
        Assert.Equal((5001, 8f, 500, 5, false, true), (halo.Id, halo.Radius, halo.TickMs, halo.TargetMax, halo.Hostile, halo.IncludeSelf));
        Assert.Equal(5002, Assert.Single(halo.Applied)); // self id 5001 filtered out

        var heal = rules.Periodic[6001];
        Assert.Equal((BuffRuleTables.PeriodicKind.Heal, 1000, 101, 350L), (heal.Kind, heal.IntervalMs, heal.HealAttr, heal.HealFlat));

        var curse = rules.Periodic[6002];
        Assert.Equal((BuffRuleTables.PeriodicKind.Damage, 2000, true, 3, 6000L), (curse.Kind, curse.IntervalMs, curse.Magic, curse.Element, curse.RatioBp));

        var burn = rules.Periodic[6003];
        Assert.Equal((BuffRuleTables.PeriodicKind.Damage, 500, false, 1, 2500L), (burn.Kind, burn.IntervalMs, burn.Magic, burn.Element, burn.RatioBp));

        var dot = rules.Periodic[6004];
        Assert.Equal((BuffRuleTables.PeriodicKind.Damage, 1000, true, 2, 3000L), (dot.Kind, dot.IntervalMs, dot.Magic, dot.Element, dot.RatioBp));
    }
}
