using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>The automatic attack after one press keeps the skill's time on average: the tick does not add up. In-game
    /// 3.11, measured: 500 ms swings came 550 ms apart.</summary>
    [ClientDataFact]
    public async Task TheAutomaticAttackKeepsTheSkillsTime()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Template = target.Template with { MaxHp = 100_000_000 }; target.Hp = 100_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        bool Mine(UseInSkillInfo n) => n.SkillID == 1000001 && n.SrcID == Session("alice").Role!.Id;
        var first = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, Mine);
        var last = first;
        for (int i = 0; i < 4; i++) last = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, n => Mine(n) && n.StartTime > last.StartTime);
        Assert.InRange((last.StartTime - first.StartTime) / 4.0, first.SkillMs - 15, first.SkillMs + 15);
    }

    [Theory]
    [InlineData(1000, 1000, 333, 1333)] // served on time
    [InlineData(1000, 1017, 333, 1333)] // the next tick served it: the plan stands, so the tick does not add up
    [InlineData(1000, 3000, 333, 3283)] // held up by another skill: no catching up
    public void TheNextSwingCountsFromThePlan(long planned, long now, int skillMs, long next) =>
        Assert.Equal(next, CombatService.NextSwing(planned, now, skillMs));
}
