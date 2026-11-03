using Romsg;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Skill 1110601 is a single-target row released without a lock (_iRelease 6, _iRangeType 1, 5 m): the
    /// target id comes from the client, and nothing but this check measures it.</summary>
    [ClientDataFact]
    public async Task AnUnlockedSingleTargetSkillDoesNotHitATargetOutOfReach()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1200");
        await Teach("alice", 11106);
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        long full = target.Hp;
        await Loop.InvokeAsync(() =>
        {
            target.X = a.Position.X + 40;
            target.Z = a.Position.Z;
            return Task.CompletedTask;
        });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110601, TargetId = target.Uid, TargetPos = a.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1500);
        Assert.Equal(full, await Loop.InvokeAsync(() => Task.FromResult(target.Hp)));

        await Loop.InvokeAsync(() =>
        {
            target.X = a.Position.X + 2;
            target.Z = a.Position.Z;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110601, TargetId = target.Uid, TargetPos = a.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.Result == 0)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(target.Hp)) == full && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(target.Hp)) < full);
    }
}
