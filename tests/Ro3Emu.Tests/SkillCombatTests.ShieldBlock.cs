using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Shield Block (Royal Guard 11638): block, counter on a second tap or when the block ends, Powerful after a hit.</summary>
public sealed partial class SkillCombatTests
{
    private const int ShieldBlockRow = 1163801, ShieldCounterRow = 1163841, PowerfulCounterRow = 1163871;
    private const int BlockParry = 1164201, PowerfulParry = 1164301;

    private static int HitsOf(Ro3Emu.Bot.BotClient bot, int row, long target) => bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
        .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Count(h => h.SkillId == row && h.Dsts.Any(d => d.DstID == target && d.Damage > 0));

    [ClientDataFact] // "Tap this skill again while blocking ... to use [Shield Auto Counter]": the counter row, and the block ends
    public async Task ASecondTapWhileBlockingCounters()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1410, 11638, ShieldBlockRow);
        await CastAtAsync(bot, ShieldBlockRow, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == BlockParry), Wait));
        Assert.Equal(ShieldCounterRow, EffectiveId(a, ShieldBlockRow));
        // The bar's counter key (11638 * 10000 + 4) is told ready, not the block's shared cooldown: the client would not send the tap.
        var told = bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillCdUpdate)).SelectMany(e => SkillCdNotify.Parser.ParseFrom(e.Body).Infos)
            .Last(i => i.SkillId == 11638 && i.SkillMode == 4);
        Assert.Equal(0L, told.CdEndTimestampMs);
        a.GmCastSkill = ShieldCounterRow;
        await CastAtAsync(bot, ShieldCounterRow, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == ShieldCounterRow && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        Assert.True(SpinWait.SpinUntil(() => !a.Buffs.Any(b => b.ConfigId == BlockParry), Wait));
        // The block ended with the tap: it does not counter again by itself.
        await Task.Run(() => SpinWait.SpinUntil(() => false, 1500));
        Assert.Equal(1, HitsOf(bot, ShieldCounterRow, dummy.Uid));
    }

    [ClientDataFact] // "... or wait until the block ends to use [Shield Auto Counter]"; attacked while blocking, it is the Powerful one
    public async Task ABlockThatEndsCountersAndAHitMakesItPowerful()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1410, 11638, ShieldBlockRow);
        await Loop.InvokeAsync(() => { a.Face = FaceTo(a.Position, dummy.Pos); return Task.CompletedTask; });
        await CastAtAsync(bot, ShieldBlockRow, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == ShieldCounterRow && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));

        Assert.True(SpinWait.SpinUntil(() => a.SkillCast == null || a.SkillCast.Cancelled || Loop.NowMs >= a.SkillCast.StartMs + a.SkillCast.ChantMs + a.SkillCast.ActionMs, Wait));
        await Loop.InvokeAsync(() => { a.Combat.SkillCds.Clear(); a.Combat.SkillModeCds.Clear(); return Task.CompletedTask; });
        await CastAtAsync(bot, ShieldBlockRow, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == BlockParry), Wait));
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() => combat.ReactToHitAsync(a, dummy, null, null, 10));
        Assert.Contains(a.Buffs, b => b.ConfigId == PowerfulParry);
        Assert.Equal(PowerfulCounterRow, EffectiveId(a, ShieldBlockRow));
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == PowerfulCounterRow && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
    }

    [ClientDataFact] // The counter of a block that runs out lands later, so a kill it makes commits under every contributor's role
    public async Task ABlockEndCounterKillWithAnotherContributorCommitsItsReward()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (bot, a, dummy) = await ReadyAsync(alice, 1410, 11638, ShieldBlockRow);
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() =>
        {
            a.Face = FaceTo(a.Position, dummy.Pos);
            combat.Dealt.Invoke(Session("bob"), dummy, 1); // Bob hit it too
            return Task.CompletedTask;
        });
        await CastAtAsync(bot, ShieldBlockRow, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == BlockParry), Wait));
        await Loop.InvokeAsync(() => { dummy.Hp = 1; return Task.CompletedTask; }); // the end's counter is the killing hit
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == ShieldCounterRow && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        Assert.True(SpinWait.SpinUntil(() => dummy.RewardSettlement?.Committed == true, Wait));
    }

    [ClientDataFact] // Absolute Defense (Soul Echo resonance): the block's form with twice the DEF and MDEF
    public async Task AbsoluteDefenseBlocksWithItsOwnForm()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1410, 11638, ShieldBlockRow);
        await HoldResonanceAsync(a, MarkOfStage3Rows);
        Assert.Contains(EmblemService.AbsoluteDefense, a.ResonanceSkills);
        Assert.Equal(11163831, EffectiveId(a, ShieldBlockRow));
        await CastAtAsync(bot, ShieldBlockRow, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == 11165801) && a.Buffs.Any(b => b.ConfigId == 11165501), Wait));
        Assert.Equal(11163861, EffectiveId(a, ShieldBlockRow));
    }

    private static float FaceTo(Vec3 from, Vec3 to) =>
        (float)(Math.Atan2(to.X - from.X, to.Z - from.Z) * 180 / Math.PI);
}
