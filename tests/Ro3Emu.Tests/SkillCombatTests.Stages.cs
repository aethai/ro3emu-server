using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // High-Energy Impact: "become Flying ... Tap the skill again or wait until the Flying duration ends to attack Below"
    public async Task HighEnergyImpactTakesOffThenLandsByItself()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16307, 1630701);
        await CastAtAsync(bot, 1630701, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1000);
        Assert.Equal(1000000, dummy.Hp); // in the air
        Assert.Contains(a.Buffs, b => b.ConfigId == 1632901);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0)));
        await Task.Delay(100);
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1632901);
        Assert.Contains(a.Buffs, b => b.ConfigId == 1633601);
    }

    [ClientDataFact] // Its show 16307 lands by itself at its attack point 3.2 s of 5.017 s, not at the action's end
    public async Task HighEnergyImpactLandsWhenItsShowDoes()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16307, 1630701);
        await CastAtAsync(bot, 1630701, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var takeOff = a.SkillCast!;
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        long landed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - takeOff.StartMs;
        int expected = (int)Math.Round(3.2 / 5.016667 * takeOff.ActionMs);
        Assert.InRange(landed, expected - 50, expected + 250);
    }

    [ClientDataFact] // "[High-Energy Pulse] ... up to @{5} (5) enemies within @{4} (8) m": eight dummies around, no hit takes more than five
    public async Task HighEnergyPulsesHitAtMostFive()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16307, 1630701);
        await Teach("alice", 16304);
        for (int i = 0; i < 7; i++)
        {
            var mark = await Target("alice", stationary: true, npcId: 5000002);
            int n = i;
            await Loop.InvokeAsync(() =>
            {
                mark.StaysOutOfBattle = true; mark.Hp = 1000000;
                mark.X = a.Position.X + 2 + n % 3; mark.Y = a.Position.Y; mark.Z = a.Position.Z + n / 3;
                return Task.CompletedTask;
            });
        }
        await CastAtAsync(bot, 1630701, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == 1633601), Wait)); // landed: the Tornado is enhanced
        Assert.True(SpinWait.SpinUntil(() => a.SkillCast == null || a.SkillCast.Cancelled
            || Loop.NowMs >= a.SkillCast.StartMs + a.SkillCast.ChantMs + a.SkillCast.ActionMs, Wait)); // and its show is over
        await Loop.InvokeAsync(() => { a.Combat.SkillCds.Clear(); a.Combat.SkillModeCds.Clear(); return Task.CompletedTask; });
        a.GmCastSkill = 1630401;
        await CastAtAsync(bot, 1630401, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => !a.Buffs.Any(b => b.ConfigId == 1633601), Wait)); // the pulses went off
        await Task.Run(() => SpinWait.SpinUntil(() => false, 1000));
        var hits = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).ToList();
        Assert.Contains(hits, h => h.Dsts.Count(d => d.Damage > 0) == 5);
        Assert.All(hits, h => Assert.True(h.Dsts.Count(d => d.Damage > 0) <= 5));
    }

    [ClientDataFact] // Move back Shadow Echo: "Cast again within 6 seconds to return to the shadow"
    public async Task ShadowEchoReturnsOnTheSecondPress()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5400"); await Teach("alice", 15329);
        var a = Session("alice");
        a.GmCastSkill = 1532901;
        var start = a.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532901, TargetPos = start, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var away = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)a.Role!.Id);
        await Task.Delay(500);
        await Loop.InvokeAsync(() => { a.Combat.SkillCds.Clear(); a.Combat.SkillModeCds.Clear(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532901, TargetPos = away.EndPos, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var back = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)a.Role!.Id);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, back.EndPos.X, back.EndPos.Z), 0f, 0.1f);
    }

    [ClientDataFact] // [Shield Stance]: "At 20 stacks, your next 3 Powerful Shield Bash": a full meter turns into three
    public async Task AFullShieldStanceMakesThreeUltraBashes()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1410");
        var a = Session("alice");
        await Loop.InvokeAsync(async () =>
        {
            a.Combat.Energy[141002].Value = 19;
            await _app.Services.GetRequiredService<BuffService>().AddShieldStanceAsync(a);
            Assert.Equal(0, a.Combat.Energy[141002].Value);
            Assert.Equal(3, a.Buffs.Single(b => b.ConfigId == 1163101).PileCount);
        });
    }
}
