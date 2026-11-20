using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>
/// Two party members land a lethal hit on the same dungeon monster (a report from a live realm: in rooms of two or more
/// the final kill could be lost and the monster's HP restored). The death is committed once, the monster stays dead,
/// and the kill is counted for one of them only.
/// </summary>
public sealed partial class DungeonTests
{
    private async Task<(BotClient Alice, BotClient Bob, long Room, MonsterUnit Target)> TwoOnOneMonsterAsync()
    {
        var request = ConfigureTwoActorStory();
        var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice, request));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        long room = await Load(alice, request.SceneEntry);
        Assert.Equal(room, await Load(bob, request.SceneEntry));
        MonsterUnit? target = null;
        for (int i = 0; i < 100 && target == null; i++)
        {
            target = await OnLoopValue(() => Service<World>().AllUnits().OfType<MonsterUnit>()
                .Where(m => m.InstanceId == room).OrderBy(m => m.Uid).FirstOrDefault());
            if (target == null) await Task.Delay(20);
        }
        Assert.NotNull(target);
        await OnLoop(async () =>
        {
            foreach (var bot in new[] { alice, bob })
            {
                await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(target.X + 1, target.Y, target.Z, target.Face));
                Service<MonsterService>().Engage(target, Session(bot));
            }
            target.Hp = 1;
        });
        return (alice, bob, room, target);
    }

    private async Task AssertKilledOnceAsync(BotClient alice, BotClient bob, MonsterUnit target)
    {
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == target.Uid && d.IsDead));
        for (int i = 0; i < 100 && !await OnLoopValue(() => target.Dead); i++) await Task.Delay(20);
        var (dead, hp, durable) = await OnLoopValue(() => (target.Dead, target.Hp, target.DeathDurable));
        Assert.True(dead);
        Assert.Equal(0, hp);
        Assert.True(durable);
        await Task.Delay(500); // a late second settlement would restore HP or count again here
        Assert.Equal(0, await OnLoopValue(() => target.Hp));
        var attempt = Session(alice).DungeonAttempt!;
        long kills = new[] { alice, bob }.Sum(bot => attempt.Stats.TryGetValue((long)bot.Self!.Uuid, out var stat)
            && stat.KillNpcData.TryGetValue(target.Template.NpcId, out var count) ? count.LongValue : 0);
        Assert.Equal(1, kills);
    }

    [ClientDataFact]
    public async Task TwoLethalHitsAtOnceKillTheMonsterOnce()
    {
        var (alice, bob, _, target) = await TwoOnOneMonsterAsync();
        await using (alice)
        await using (bob)
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
            await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser);
            await AssertKilledOnceAsync(alice, bob, target);
        }
    }

    [ClientDataFact]
    public async Task ASecondLethalHitWhileTheFirstKillCommitsLeavesTheMonsterDead()
    {
        var (alice, bob, _, target) = await TwoOnOneMonsterAsync();
        var combat = Service<CombatService>();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<GameSession, MonsterUnit, Task> hold = (s, m) =>
        {
            if (!ReferenceEquals(m, target) || s.Role?.Id != (long)alice.Self!.Uuid) return Task.CompletedTask;
            holding.TrySetResult();
            return release.Task; // Alice holds the room's death gate, her reward not yet committed
        };
        combat.BeforeKilled.Insert(0, hold);
        try
        {
            await using (alice)
            await using (bob)
            {
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await holding.Task.WaitAsync(Wait);
                await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
                await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser);
                await Task.Delay(1500); // Bob's hit lands on the 0 HP monster and queues at the death gate
                release.TrySetResult();
                await AssertKilledOnceAsync(alice, bob, target);
            }
        }
        finally
        {
            release.TrySetResult();
            combat.BeforeKilled.Remove(hold);
        }
    }
}
