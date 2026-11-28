using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Debuffs on monsters: which count (BuffService.CountsInUnitStats) and what the hit formula reads from them.</summary>
public sealed partial class SkillCombatTests
{
    private sealed class SteadyRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override double DamageFloat() => 0.5;
        public override int Proc(int max) => 0;
    }

    private const int WhiteImprisonBuff = 1230101, DarkIllusionSlow = 1530201, BossFrenzy = 5001001;

    private async Task<(MonsterUnit Dummy, CombatService Combat, BuffService Buffs, long Role)> DebuffDummyAsync()
    {
        var dummy = await Target("alice", stationary: true, npcId: 5000002);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new SteadyRolls();
        await Loop.InvokeAsync(() => { dummy.StaysOutOfBattle = true; dummy.Hp = 1000000; return Task.CompletedTask; });
        return (dummy, combat, _app.Services.GetRequiredService<BuffService>(), Session("alice").Role!.Id);
    }

    private long HitOn(CombatService combat, MonsterUnit dummy) => combat.Damage(
        new CombatService.Fighter(5000, 5000, 1000, 0, 99, 1), combat.MonsterFighter(dummy), _app.Services.GetRequiredService<GameData>()
            .Row("SkillConfig", NoviceAttack)!.Value).Damage;

    [ClientDataFact] // White Imprison (1230101, _iType 0): "+5% Physical and Magic damage taken" now reaches the hit formula
    public async Task AnOtherUnitsTypeZeroDebuffRaisesTheMonstersDamageTaken()
    {
        await using var alice = await InWorld("Alice");
        var (dummy, combat, buffs, role) = await DebuffDummyAsync();
        long before = await Loop.InvokeAsync(() => Task.FromResult(HitOn(combat, dummy)));
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(dummy, WhiteImprisonBuff, 60000, role));
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(buffs.HasUnitCombatBuffs(dummy))));
        long after = await Loop.InvokeAsync(() => Task.FromResult(HitOn(combat, dummy)));
        Assert.InRange(after, before * 104 / 100, before * 106 / 100);
        // Its element counter resistance stays the template's once the debuffed sheet is read.
        Assert.Equal(dummy.Template.ElementCounterResistance,
            await Loop.InvokeAsync(() => Task.FromResult(combat.MonsterStats(dummy)[AttributeId.EleReduce])));
    }

    [ClientDataFact] // Dark Illusion's slow (_iType 0, all reductions) counts; a raising Frenzy or the monster's own buff does not
    public async Task OnlyReductionsFromOtherUnitsCountOnAMonster()
    {
        await using var alice = await InWorld("Alice");
        var (dummy, combat, buffs, role) = await DebuffDummyAsync();
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(dummy, BossFrenzy, 60000, role));
        Assert.False(await Loop.InvokeAsync(() => Task.FromResult(buffs.HasUnitCombatBuffs(dummy))));
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(dummy, DarkIllusionSlow, 60000, dummy.Uid)); // its own
        Assert.False(await Loop.InvokeAsync(() => Task.FromResult(buffs.HasUnitCombatBuffs(dummy))));
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(dummy, DarkIllusionSlow, 60000, role));
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(buffs.HasUnitCombatBuffs(dummy))));
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(combat.MonsterStats(dummy)[AttributeId.Speed] < dummy.Template.SpeedCm)));
    }
}
