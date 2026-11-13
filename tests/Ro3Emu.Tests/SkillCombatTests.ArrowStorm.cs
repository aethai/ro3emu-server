using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int ArrowStormGroup = 13302, ArrowStormRow = 1330201, ArrowStormHandGlow = 1330203, ArrowStormRing = 1330204, ArrowStormBursts = 1330208;
    private const int DisplayGfx = 2; // enum_DisplayEffectType.Gfx

    private static SkillEffectDisplayNotify[] EffectsOf(Ro3Emu.Bot.BotClient bot, long skill) => [.. bot.Inbox.Snapshot()
        .Where(e => e.IsGame(CombatService.SkillEffect)).Select(e => SkillEffectDisplayNotify.Parser.ParseFrom(e.Body)).Where(x => x.SkillId == skill)];

    [ClientDataFact] // Arrow Storm's ring, bursts and hand glow (show 13302 range, played by no timeline) come with the cast; its 31101 is unchanged
    public async Task ArrowStormShowsItsGroundWithTheCast()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 3400, ArrowStormGroup, ArrowStormRow);
        await CastAtAsync(bot, ArrowStormRow, dummy);
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);

        var effects = EffectsOf(bot, ArrowStormRow);
        Assert.Equal([ArrowStormHandGlow, ArrowStormRing, ArrowStormBursts], effects.Select(x => x.EffectId));
        Assert.All(effects, x =>
        {
            Assert.Equal(DisplayGfx, x.EffectType);
            Assert.Equal(a.Role!.Id, x.MasterId);
        });
        // The client plays a Gfx on its source unit and at targetPos only without one: the glow is the caster's, the ground has none.
        Assert.Equal([a.Role!.Id, 0L, 0L], effects.Select(x => x.SourceId));
        Assert.Equal(dummy.Pos.X, effects[1].TargetPos.X, 3);
        Assert.Equal(dummy.Pos.Z, effects[2].TargetPos.Z, 3);
        Assert.Equal(a.Position.X, effects[0].TargetPos.X, 3);

        Assert.Equal(ArrowStormRow, hit.SkillId);
        Assert.Contains(hit.Dsts, d => d.DstID == dummy.Uid && d.Damage > 0);
        Assert.DoesNotContain(hit.ShowPoint, new[] { ArrowStormHandGlow, ArrowStormRing, ArrowStormBursts });
    }
}
