using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>The wheel slot the client was last sent (5025 skillWheel) and its own skill (originSkillWheel).</summary>
    private static (int Shown, int Origin) SentSlot(GameSession s, int slot)
    {
        var sent = (DBSkillData)s.Synced[SkillService.SkillDataSync];
        return (sent.SkillWheel.WhellSkills.GetValueOrDefault(slot)?.SkillConfigId ?? 0, sent.OriginSkillWheel.WhellSkills.GetValueOrDefault(slot)?.SkillConfigId ?? 0);
    }

    private async Task PutOnBarAsync(GameSession s, int slot, int group) => await Loop.InvokeAsync(async () =>
    {
        s.Progress!.SkillBar[slot] = group;
        var plan = s.SkillPlans!.SkillAssignPlans[s.SkillPlans.CurrentPlanIndex];
        while (plan.ManualLoadSkill.Count < slot) plan.ManualLoadSkill.Add(new SkillData());
        plan.ManualLoadSkill[slot - 1] = new SkillData { SkillId = group, SkillLevel = 1 };
        await _app.Services.GetRequiredService<SkillService>().SyncAsync(s);
    });

    [ClientDataFact] // Shadow Guard gives "Thousand Shadows Status" [1532201], Thousand Shadows "Shadow Loop Status" [1532001]; no tree teaches 15322 or 15320
    public async Task ShadowGuardLeadsToThousandShadowsThenShadowLoop()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 5400, 15323, 1532301);
        await PutOnBarAsync(a, 2, 15323);
        Assert.Equal((1532301, 1532301), SentSlot(a, 2));

        await CastAtAsync(bot, 1532301, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await bot.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync) && SentSlot(a, 2).Shown == 1532201, Wait);
        Assert.Equal((1532201, 1532301), SentSlot(a, 2));

        await Task.Delay(600); // Shadow Guard's action time
        await CastAtAsync(bot, 1532201, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1532201 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0)));
        await bot.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync) && SentSlot(a, 2).Shown == 1532001, Wait);
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1532201); // spent by the press it offered

        await Task.Delay(600);
        await CastAtAsync(bot, 1532001, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await bot.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync) && SentSlot(a, 2).Shown == 1532301, Wait);

        // The chain is spent: Shadow Loop is not offered again.
        await Task.Delay(600);
        await CastAtAsync(bot, 1532001, dummy);
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact] // Thousand Shadows without its status is refused, as before
    public async Task ThousandShadowsNeedsShadowGuardFirst()
    {
        await using var alice = await InWorld("Alice");
        var (bot, _, dummy) = await ReadyAsync(alice, 5400, 15323, 1532301);
        await CastAtAsync(bot, 1532201, dummy);
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact] // Move back Shadow Echo's slot shows its return row while "Move back Afterimage Status" [1533001] lasts, and that row returns
    public async Task ShadowEchoReturnsThroughTheRowItsStatusNames()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5400"); await Teach("alice", 15329);
        var a = Session("alice");
        a.GmCastSkill = 1532901;
        await PutOnBarAsync(a, 3, 15329);
        var start = a.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532901, TargetPos = start, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var away = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)a.Role!.Id);
        await alice.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync) && SentSlot(a, 3).Shown == 1533001, Wait);
        await Task.Delay(500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1533001, TargetPos = away.EndPos, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var back = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)a.Role!.Id);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, back.EndPos.X, back.EndPos.Z), 0f, 0.1f);
        await alice.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync) && SentSlot(a, 3).Shown == 1532901, Wait);
        Assert.Contains(a.SkillModeCds, c => c.Key.Group == 15329 && c.Value > MonsterService.NowMs); // its own cooldown starts at the return
    }
}
