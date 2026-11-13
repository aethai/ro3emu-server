using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class SureProcs : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override int Proc(int max) => 0;
    }

    private async Task<(Ro3Emu.Bot.BotClient Bot, GameSession Session, MonsterUnit Dummy)> ReadyAsync(Ro3Emu.Bot.BotClient alice, int job, int group, int skill)
    {
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job " + job); await Teach("alice", group);
        var a = Session("alice");
        a.GmCastSkill = skill;
        _app.Services.GetRequiredService<CombatService>().Rolls = new SureProcs();
        var dummy = await Target("alice", stationary: true, npcId: 5000002);
        await Loop.InvokeAsync(() =>
        {
            dummy.StaysOutOfBattle = true; dummy.Hp = 1000000;
            dummy.X = a.Position.X + 1; dummy.Y = a.Position.Y; dummy.Z = a.Position.Z;
            return Task.CompletedTask;
        });
        return (alice, a, dummy);
    }

    private static async Task CastAtAsync(Ro3Emu.Bot.BotClient bot, int skill, MonsterUnit target) =>
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos, Face = 90 });

    [ClientDataFact] // Envenom: "applies Poisoned damage": a second hit, of its row 1511601
    public async Task EnvenomLandsItsPoisonedHit()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 1500, 15005, 1500501);
        await CastAtAsync(bot, 1500501, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var poisoned = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1511601 && h.Dsts.Any(d => d.DstID == dummy.Uid));
        Assert.True(poisoned.Dsts.Single().Damage > 0);
    }

    [ClientDataFact] // Crimson Rock: "a 60% Chance to trigger Meteorite"
    public async Task CrimsonRockTriggersMeteorite()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 2300, 12312, 1231201);
        await CastAtAsync(bot, 1231201, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1235001 && h.Dsts.Any(d => d.DstID == dummy.Uid)));
    }

    [ClientDataFact] // Turn Undead: "a Normal Undead Monster ... kill it instantly"; a dummy of another element only takes its hit
    public async Task TurnUndeadKillsOnlyANormalUndead()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 1400, 14118, 1411801);
        Assert.NotEqual(10, dummy.Template.Element);
        await CastAtAsync(bot, 1411801, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid));
        await Task.Delay(100);
        Assert.True(dummy.Hp > 0);
    }

    [ClientDataFact] // A held Turn Undead (HoldContinue: a press every 0.2 s) let in by the slack before its hit keeps that hit
    public async Task AHeldTurnUndeadKeepsEveryHit()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1400, 14118, 1411801);
        await CastAtAsync(bot, 1411801, dummy);
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = a.SkillCast!;
        var show = _app.Services.GetRequiredService<GameData>().SkillTemplate(new SkillConfig(first.Row).ScriptShow);
        int yield = SkillShow.InterruptOffset(show, first.ChantMs, first.ActionMs)!.Value;
        while (MonsterService.NowMs < first.StartMs + yield - CombatService.SwingJitterMs + 10) await Task.Delay(2);
        await Loop.InvokeAsync(() => { a.SkillCds.Clear(); a.SkillModeCds.Clear(); return Task.CompletedTask; });
        await CastAtAsync(bot, 1411801, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser, r => r.SessionId != accepted.SessionId)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == dummy.Uid)));
    }

    [ClientDataFact] // Letting go of a key whose press still waits on its reply drops that press (CancelInputSkill), not the running cast
    public async Task ReleasingAHeldTurnUndeadKeepsTheCastItInterruptsNothingOf()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1400, 14118, 1411801);
        await CastAtAsync(bot, 1411801, dummy);
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await CastAtAsync(bot, 1411801, dummy); // the held key's next press, refused while the first one chants
        Assert.NotEqual(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser, r => r.SessionId == 0)).Result);
        await bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = 1411801 });
        Assert.False((await Next(bot, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == dummy.Uid)));
        Assert.False(a.SkillCast?.Cancelled ?? false);
    }

    [ClientDataFact] // A key held for 12 s (a press every 0.2 s): every Turn Undead it started hits, at the slack's edge too
    public async Task EveryCastOfAHeldTurnUndeadHits()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 1400, 14118, 1411801);
        for (int i = 0; i < 60; i++)
        {
            await CastAtAsync(bot, 1411801, dummy);
            await Task.Delay(200);
        }
        await Task.Delay(2500);
        var sessions = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseSkill)).Select(e => CastSkillRsp.Parser.ParseFrom(e.Body))
            .Where(r => r.Result == 0).Select(r => r.SessionId).ToList();
        var hit = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .Where(h => h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0)).Select(h => h.SessionId).ToHashSet();
        Assert.True(sessions.Count >= 8, $"{sessions.Count} casts");
        Assert.All(sessions, id => Assert.Contains(id, hit));
    }

    [ClientDataFact] // #433: a kept hit that kills lands outside the character lock its kill reward takes, so the next cast still answers
    public async Task AHeldTurnUndeadThatKillsDoesNotStallTheNextCast()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, _) = await ReadyAsync(alice, 1400, 14118, 1411801);
        var mob = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            mob.Hp = 1000000; mob.X = a.Position.X + 1; mob.Y = a.Position.Y; mob.Z = a.Position.Z;
            return Task.CompletedTask;
        });
        await CastAtAsync(bot, 1411801, mob);
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = a.SkillCast!;
        var show = _app.Services.GetRequiredService<GameData>().SkillTemplate(new SkillConfig(first.Row).ScriptShow);
        int yield = SkillShow.InterruptOffset(show, first.ChantMs, first.ActionMs)!.Value;
        while (MonsterService.NowMs < first.StartMs + yield - CombatService.SwingJitterMs + 10) await Task.Delay(2);
        await Loop.InvokeAsync(() => { a.SkillCds.Clear(); a.SkillModeCds.Clear(); mob.Hp = 1; return Task.CompletedTask; });
        await CastAtAsync(bot, 1411801, mob);
        var second = Next(bot, CombatService.UseSkill, CastSkillRsp.Parser, r => r.SessionId != accepted.SessionId);
        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.True(mob.Hp <= 0);
    }

    [ClientDataFact] // Heavy Cannon Shot: "The next time you Deals DMG, removes Charge Up and triggers Charge Up Strike"
    public async Task ChargeUpGoesOffOnTheNextHit()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1100, 10010, 1001001);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1160405, 8000, a.Role!.Id));
        await CastAtAsync(bot, 1001001, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1162205 && h.Dsts.Any(d => d.DstID == dummy.Uid)));
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1160405);
    }

    [ClientDataFact] // Warg Bite: "resetting the Cooldown of Warg Hunt"
    public async Task WargBiteResetsWargHunt()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1330, 13325, 1332501);
        await Loop.InvokeAsync(() => { a.SkillCds[13323] = MonsterService.NowMs + 60000; return Task.CompletedTask; });
        await CastAtAsync(bot, 1332501, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (a.SkillCds.ContainsKey(13323) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(a.SkillCds.ContainsKey(13323));
    }

    [ClientDataFact] // Crescent Wheel: "For every 1 enemies hit, reduce Axe Tornado's Cooldown by 2 seconds"
    public async Task CutCooldownTakesTimeOffAndEndsPastItsEnd()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        long start = MonsterService.NowMs;
        await Loop.InvokeAsync(async () =>
        {
            a.SkillCds[16304] = start + 8000;
            await CombatService.CutCooldownAsync(a, 16304, 2000);
            Assert.Equal(start + 6000, a.SkillCds[16304]);
            await CombatService.CutCooldownAsync(a, 16304, 60000);
            Assert.False(a.SkillCds.ContainsKey(16304));
        });
    }

    // Warg Strike's wolf, as a capture of the Genesis server has it for a manual cast: the cast on 31106, the charge as a
    // 31108 bullet (1331605) and a 31109 PendantShow (1331602), and the hit's showPoint 1331204.
    private const int WargStrikeRow = 1331201, WolfBullet = 1331605, WolfEffect = 1331602, WargHitPoint = 1331204;

    [ClientDataFact]
    public async Task AManualWargStrikeSendsTheWolfsCharge()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 3400, 13312, WargStrikeRow);
        await CastAtAsync(bot, WargStrikeRow, dummy);
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(bot, CombatService.UseInSkillHigh, UseInSkillInfo.Parser, m => m.SessionId == accepted.SessionId);
        var bullet = await Next(bot, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser, m => m.SkillConfigId == WargStrikeRow);
        Assert.Equal(WolfBullet, bullet.EffectId);
        var wolf = await Next(bot, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, m => m.SkillId == WargStrikeRow);
        Assert.Equal(WolfEffect, wolf.EffectId);
        Assert.Equal(5, wolf.EffectType); // enum_DisplayEffectType.PendantShow
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(WargHitPoint, hit.ShowPoint);
    }

    // The automatic Warg Strike of Focused Arrow Strike: on the Genesis server its wolf charges the target as for a manual cast
    // (Genesis capture, Discord report), on the extra high-level timeline without the rider's action; its hit shares that session.
    [ClientDataFact]
    public async Task TheAutomaticWargStrikeSendsTheWolfsCharge()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 3400, 13305, 1330501);
        await Teach("alice", 13312);
        await CastAtAsync(bot, 1330501, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var timeline = await Next(bot, CombatService.ExtraUseInSkillHigh, UseInSkillInfo.Parser, m => m.SkillID == WargStrikeRow);
        Assert.False(timeline.PlayAct);
        Assert.Equal(WolfBullet, (await Next(bot, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser, m => m.SkillConfigId == WargStrikeRow)).EffectId);
        var wolf = await Next(bot, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser, m => m.SkillId == WargStrikeRow);
        Assert.Equal(WolfEffect, wolf.EffectId);
        Assert.Equal(dummy.Uid, wolf.TargetId);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == WargStrikeRow && h.Dsts.Any(d => d.DstID == dummy.Uid));
        Assert.Equal(timeline.SessionId, hit.SessionId);
        Assert.Equal(WargHitPoint, hit.ShowPoint);
    }
}
