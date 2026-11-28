using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Skill 1111201 has a sure Stun in its _kBuff2 (901601, 10000, 1000): it lands on an enemy player as on a monster.</summary>
    [ClientDataFact]
    public async Task ASkillsHostileBuffLandsOnTheEnemyPlayerItHits()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1200");
        await Teach("alice", 11112);
        await Gm("bob", "!lvl 99");
        var source = Session("alice");
        var victim = Session("bob");
        var start = source.Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            victim.Position = new Vec3 { X = start.X + 2, Y = start.Y, Z = start.Z };
            Combat.AddRuleForTest(new DelegateCombatRule { PlayerHostile = (a, b) => a == source && b == victim });
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1111201, TargetId = victim.Role!.Id, TargetPos = victim.Position });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        bool Stunned() => victim.Buffs.Any(b => b.ConfigId == 901601);
        while (!await Loop.InvokeAsync(() => Task.FromResult(Stunned())) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Stunned())));
    }

    /// <summary>Explosive Spear's "and 2 Other enemies within 4 meters of the target" reaches the players around an enemy player
    /// it locks, as it does monsters: the target and one beside it, not one 7 m away.</summary>
    [ClientDataFact]
    public async Task ATextAreaHitsThePlayersAroundItsLockedPlayer()
    {
        var (bot, me, _) = await Caster("Alice", 1200, 11110, 1, 10102115);
        await using var _alice = bot;
        await using var bob = await InWorld("Bob");
        await using var charlie = await InWorld("Charlie");
        await using var dave = await InWorld("Dave");
        var target = Session("bob");
        var beside = Session("charlie");
        var away = Session("dave");
        var start = me.Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            target.Position = new Vec3 { X = start.X + 2, Y = start.Y, Z = start.Z };
            beside.Position = new Vec3 { X = start.X + 3, Y = start.Y, Z = start.Z };
            away.Position = new Vec3 { X = start.X + 9, Y = start.Y, Z = start.Z };
            Combat.AddRuleForTest(new DelegateCombatRule { PlayerHostile = (a, b) => a == me && (b == target || b == beside || b == away) });
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1111001, TargetId = target.Role!.Id, TargetPos = target.Position });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        bool Hurt(GameSession p) => bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Any(e => SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == p.Role!.Id && d.Damage > 0));
        Assert.True(SpinWait.SpinUntil(() => Hurt(target) && Hurt(beside), Wait));
        await Task.Run(() => SpinWait.SpinUntil(() => false, 500));
        Assert.False(Hurt(away));
    }
}
