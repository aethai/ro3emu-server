using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int HealSkill = 1400701, HalfHealingTaken = 1333503; // BuffConfig 1333503: 1208 -5000

    private sealed class CritRolls(double crit) : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => crit;
    }

    private async Task<(long Amount, DamageHitResult Result)> HealBob(Ro3Emu.Bot.BotClient alice, Ro3Emu.Bot.BotClient bob)
    {
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("bob"), 1, null));
        Session("alice").SkillCds.Clear();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = HealSkill, TargetId = (long)bob.Self!.Uuid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.Result == 0);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        var heal = Assert.Single(hit.Dsts, d => d.DstID == (long)bob.Self.Uuid);
        Assert.Equal(DamageType.Heal, heal.DamageType);
        await Task.Delay(1500);
        return (heal.Damage, heal.HitResult);
    }

    [ClientDataFact]
    public async Task AnOrdinaryBuffChangesHowMuchAHealGives()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14007);
        await Gm("bob", "!lvl 60 40");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var before = await HealBob(alice, bob);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(Session("bob"), HalfHealingTaken, 60000, Session("alice").Role!.Id));
        var after = await HealBob(alice, bob);
        Assert.Equal(DamageHitResult.Normal, before.Result);
        Assert.InRange(after.Amount, before.Amount / 2 - 1, before.Amount / 2 + 1);
    }

    [ClientDataFact]
    public async Task AHealCritsByTheCharactersCritAndItsRow()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14007);
        await Gm("bob", "!lvl 60 40");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var plain = await HealBob(alice, bob);
        combat.Rolls = new CritRolls(-1);
        var crit = await HealBob(alice, bob);
        Assert.Equal(DamageHitResult.Critical, crit.Result);
        Assert.InRange(crit.Amount, (long)(plain.Amount * 1.5) - 1, (long)(plain.Amount * 1.5) + 1);
    }

    [Theory]
    [InlineData(1000, 0, 10000, 0.05)] // CRI * 0.5 / 10000
    [InlineData(1000, 500, 10000, 0.10)] // crit chance % adds
    [InlineData(1000, 0, 0, 0.0)] // a row that cannot crit
    [InlineData(0, -500, 10000, 0.0)] // not below zero
    public void AHealsCritChanceFollowsTheClientsFormula(long cri, long chance, long skillCri, double rate) =>
        Assert.Equal(rate, CombatService.HealCritRate(cri, chance, skillCri), 6);

    [Theory]
    [InlineData(0, 1.5)]
    [InlineData(2000, 1.6)] // half of Crit Damage counts, FormulaParamHealCriC 0.5
    public void AHealsCritAmountFollowsTheClientsFormula(long critDamage, double multiplier) =>
        Assert.Equal(multiplier, CombatService.HealCritMultiplier(critDamage), 6);

    /// <summary>BuffConfig 1601101: STR +5. AttributeConversionConfig 1 gives a melee job ATK and DEF per point, as a point spent does.</summary>
    [ClientDataFact]
    public async Task StrengthFromABuffRaisesWhatStrengthRaises()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var a = Session("alice");
        var before = Character.Stats(a);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1601101, 60000, a.Role!.Id));
        var after = Character.Stats(a);
        Assert.Equal(before[1] + 5, after[1]);
        Assert.True(after[AttributeId.Atk] >= before[AttributeId.Atk] + 5, $"{before[AttributeId.Atk]} -> {after[AttributeId.Atk]}");
        Assert.True(after[AttributeId.Def] > before[AttributeId.Def], $"{before[AttributeId.Def]} -> {after[AttributeId.Def]}");
    }

    /// <summary>BuffConfig 15998100: 205 +3000. Skill 1109901 has a 7 s cooldown.</summary>
    [ClientDataFact]
    public async Task ACooldownAttributeShortensTheCooldownTheClientIsTold()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11099);
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 15998100, 60000, a.Role!.Id));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1109901, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var cooldown = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser, n => n.Infos.Any(i => i.SkillId == 11099));
        var info = cooldown.Infos.First(i => i.SkillId == 11099);
        Assert.Equal(4900, info.CdEndTimestampMs - info.CdStartTimestampMs);
    }

    [Theory]
    [InlineData(7000, 3000, 4900)]
    [InlineData(7000, -9900, 13930)] // a debuff lengthens it
    [InlineData(7000, 20000, 0)] // never below zero
    public void ACooldownShareFollowsTheAttribute(long ms, long percent, long reduced) =>
        Assert.Equal(reduced, CombatService.ReducedCooldown(ms, percent));

    /// <summary>BuffConfig 1420410: 1232 -1000, a talent's SP cost cut, from an ordinary buff.</summary>
    [ClientDataFact]
    public async Task AnOrdinaryBuffChangesASkillsSpCost()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4100"); await Teach("alice", 14001);
        var a = Session("alice");
        a.NoRegenUntilMs = long.MaxValue;
        a.GmCombat |= GmCombatSwitches.NoCooldown;
        async Task<long> SpSpent()
        {
            long before = Character.Stats(a)[AttributeId.Sp];
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400101 });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Task.Delay(2500);
            return before - Character.Stats(a)[AttributeId.Sp];
        }
        long plain = await SpSpent();
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1420410, 60000, a.Role!.Id));
        long cut = await SpSpent();
        Assert.True(plain > 0);
        Assert.Equal((long)Math.Ceiling(plain * 0.9), cut);
    }
}
