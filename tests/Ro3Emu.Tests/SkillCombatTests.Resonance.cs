using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Held Soul Echo resonance rows (issue #608): the skill forms they switch to and the casts they count.</summary>
public sealed partial class SkillCombatTests
{
    private const int FourMarkEcho = 1850101; // four mark slots
    private const long MarkOfStage2Rows = 1003, MarkOfStage3Rows = 1004; // EmblemMarkConfig 9 (13200002) and 12 (13200003), six marks each
    private const int Attunement = 1234001;

    /// <summary>Four echoes, each with the mark in all four slots (16 marks: every stage of it), then the held rows refreshed.</summary>
    private Task HoldResonanceAsync(GameSession s, long mark) => Loop.InvokeAsync(async () =>
    {
        var plan = s.Profile.Emblems.Plans[s.Profile.Emblems.CurrentPlan];
        plan.Slots.Clear();
        s.Items.RemoveAll(i => i.Entry == FourMarkEcho);
        for (int slot = 1; slot <= 4; slot++)
        {
            var item = new InvItem
            {
                Entry = FourMarkEcho, Stack = 1, Pack = 5, Sort = 100 + slot, Guid = 9_100_000 + slot,
                Attrs = new() { [1001] = 0, [10001] = 2, [10002] = slot },
            };
            for (int i = 0; i < 4; i++) item.Attrs[1021 + i] = mark;
            s.Items.Add(item);
            plan.Slots[slot] = item.Guid;
        }
        await _app.Services.GetRequiredService<EmblemService>().RefreshEffectsAsync(s);
    });

    private int Attuned(GameSession s) => s.Buffs.Where(b => b.ConfigId == Attunement && b.ExpireMs > Loop.NowMs).Sum(b => b.PileCount);

    private long EffectiveId(GameSession s, int row) => Effective(s, row).Id;

    [ClientDataFact] // The stage-2 and stage-3 rows switch their skills to the forms they describe, and only for their job line
    public async Task ResonanceRowsSwitchTheSkillsTheyDescribe()
    {
        await using var alice = await InWorld("Alice");
        var (_, a, _) = await ReadyAsync(alice, 2400, 12316, 1231601);
        Assert.Equal(1231601, EffectiveId(a, 1231601));
        await HoldResonanceAsync(a, MarkOfStage3Rows);
        Assert.Contains(EmblemService.MixedAccompaniment, a.ResonanceSkills);
        Assert.Equal(1231611, EffectiveId(a, 1231601)); // Tetra Vortex, +50% on each hit
        Assert.Equal(1232611, _app.Services.GetRequiredService<CombatService>().ModeRow(a, 12326, 1) is { } copy
            ? GameData.Int(copy, "_iID") : 0); // its Reading copy
        await Gm("alice", "!job 5400");
        await Loop.InvokeAsync(() => Task.CompletedTask);
        Assert.DoesNotContain(EmblemService.MixedAccompaniment, a.ResonanceSkills);
        Assert.Contains(EmblemService.Encore, a.ResonanceSkills);
        Assert.Equal(1231601, EffectiveId(a, 1231601));
    }

    [ClientDataFact] // Elemental Ensemble: five casts of Chain Lightning give a stack; Jack Frost takes its attuned form, adds its hit and spends it
    public async Task ElementalEnsembleAttunesTheNextJackFrost()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 2400, 12313, 1231301);
        await Teach("alice", 12308);
        await HoldResonanceAsync(a, MarkOfStage2Rows);
        Assert.Contains(EmblemService.ElementalEnsemble, a.ResonanceSkills);
        for (int i = 0; i < 4; i++) await CastAndWait(bot, 1231301, dummy);
        Assert.Equal(0, Attuned(a));
        await CastAndWait(bot, 1231301, dummy);
        Assert.Equal(1, Attuned(a));
        Assert.Equal(1230811, EffectiveId(a, 1230801));

        a.GmCastSkill = 1230801;
        await CastAtAsync(bot, 1230801, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        // The attuned form's own hit, then the added MATK * 90% hit of the same row.
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1230811 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1230811 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        Assert.True(SpinWait.SpinUntil(() => Attuned(a) == 0, Wait));
        Assert.Equal(0, Attuned(a));
        Assert.Equal(1230801, EffectiveId(a, 1230801));
    }

    [ClientDataFact] // Mixed Accompaniment: a Tetra Vortex in its resonance form ends with three stacks that last until spent
    public async Task MixedAccompanimentGrantsThreeStacks()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 2400, 12316, 1231601);
        await HoldResonanceAsync(a, MarkOfStage3Rows);
        await CastAtAsync(bot, 1231601, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1231611 && h.Dsts.Any(d => d.DstID == dummy.Uid));
        Assert.True(SpinWait.SpinUntil(() => Attuned(a) >= 3, Wait));
        Assert.Equal(3, Attuned(a));
        Assert.True(a.Buffs.Single(b => b.ConfigId == Attunement).UntilSpent);
    }

    [ClientDataFact] // Encore: Shadow Pierce grants Delusion for 3 seconds
    public async Task EncoreShadowPierceGrantsDelusion()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 5400, 15326, 1532601);
        await HoldResonanceAsync(a, MarkOfStage3Rows);
        Assert.Equal(1532611, EffectiveId(a, 1532601));
        await CastAndWait(bot, 1532601, dummy);
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == 11533201), Wait));
        var delusion = a.Buffs.Single(b => b.ConfigId == 11533201);
        Assert.InRange(delusion.ExpireMs - Loop.NowMs, 1, 3000);
    }

    [ClientDataFact] // Electro Rock: a Rain of Spears charge comes after four Precision Thrust casts instead of six
    public async Task ElectroRockChargesRainOfSpearsAtFour()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1400, 11315, 1131501);
        await HoldResonanceAsync(a, MarkOfStage3Rows);
        Assert.Contains(EmblemService.ElectroRock, a.ResonanceSkills);
        await Loop.InvokeAsync(() =>
        {
            a.Combat.Energy[140001].Value = 3; a.Combat.Energy[140002].Value = 0;
            a.Combat.Energy[140002].NextRecoverMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000;
            return Task.CompletedTask;
        });
        await CastAndWait(bot, 1131501, dummy);
        Assert.Equal(0, a.Combat.Energy[140001].Value);
        Assert.Equal(1, a.Combat.Energy[140002].Value);
    }
    [ClientDataFact] // Pixel Thrust and Guardian's Might: Phantom Thrust and Reflect Damage's Thorn take their resonance rows
    public async Task PixelThrustAndGuardiansMightSwitchTheirSkills()
    {
        await using var alice = await InWorld("Alice");
        var (_, a, _) = await ReadyAsync(alice, 1400, 11308, 1130801);
        await HoldResonanceAsync(a, MarkOfStage2Rows);
        Assert.Equal(1130811, EffectiveId(a, 1130801));
        await Gm("alice", "!job 1410");
        Assert.Contains(EmblemService.GuardiansMight, a.ResonanceSkills);
        var thorn = _app.Services.GetRequiredService<CombatService>().ModeRow(a, 11618, 1);
        Assert.Equal(1161811, thorn is { } row ? GameData.Int(row, "_iID") : 0);
    }

    [ClientDataFact] // Baptism: each Judex launches one more phrase per Voice Part stack, and each landed phrase adds a stack
    public async Task BaptismPhrasesGrowWithVoicePart()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 4400, 14309, 1430901);
        await HoldResonanceAsync(a, MarkOfStage3Rows);
        Assert.Contains(EmblemService.Baptism, a.ResonanceSkills);
        await CastAtAsync(bot, 1430901, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 11431701 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == 11431701) && a.Buffs.Any(b => b.ConfigId == 11431301), Wait));
        Assert.Equal(1, a.Buffs.Single(b => b.ConfigId == 11431701).PileCount);
        Assert.Contains(a.Buffs, b => b.ConfigId == 11431301);

        await CastAndWait(bot, 1430901, dummy);
        Assert.True(SpinWait.SpinUntil(() => bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Any(h => h.SkillId == 11431801), Wait));
        Assert.True(SpinWait.SpinUntil(() => a.Buffs.Any(b => b.ConfigId == 11431701 && b.PileCount == 3), Wait));
    }

    [ClientDataFact] // Organum: when Holy Stigma's show ends, Organum strikes its area
    public async Task OrganumFollowsHolyStigma()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 4400, 14314, 1431401);
        await HoldResonanceAsync(a, MarkOfStage2Rows);
        Assert.Contains(EmblemService.Organum, a.ResonanceSkills);
        await CastAtAsync(bot, 1431401, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 11431101 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0)));
    }

    [ClientDataFact] // Organum lands later, so a kill it makes commits under every contributor's role, not only the caster's
    public async Task AnOrganumKillWithAnotherContributorCommitsItsReward()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (bot, a, dummy) = await ReadyAsync(alice, 4400, 14314, 1431401);
        await HoldResonanceAsync(a, MarkOfStage2Rows);
        var b = Session("bob");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() => { combat.Dealt.Invoke(b, dummy, 1); return Task.CompletedTask; }); // Bob hit it too
        await CastAtAsync(bot, 1431401, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1431401 && h.Dsts.Any(d => d.DstID == dummy.Uid));
        await Loop.InvokeAsync(() => { dummy.Hp = 1; return Task.CompletedTask; }); // Organum, 3 s on, is the killing hit
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 11431101 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0)));
        Assert.True(SpinWait.SpinUntil(() => dummy.RewardSettlement?.Committed == true, Wait));
    }

    [ClientDataFact] // Melody Cannon: Arm Cannon takes its Melody form, and its hit leaves a bubble that bursts when its time ends
    public async Task MelodyCannonBubbleBurstsAtItsEnd()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16312, 1631201);
        await HoldResonanceAsync(a, MarkOfStage2Rows);
        Assert.Equal(1631221, EffectiveId(a, 1631201));
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await CastAtAsync(bot, 1631201, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(SpinWait.SpinUntil(() => buffs.UnitPiles(dummy, 16316) > 0, Wait));
        // The channel's hits land within the bubble's 5 seconds; the burst, the cannon row with PATK * 300%, comes as it ends.
        int Hits() => bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .Count(h => h.SkillId == 1631221 && h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        Assert.True(SpinWait.SpinUntil(() => Hits() >= 4, Wait));
        int channel = Hits();
        Assert.True(SpinWait.SpinUntil(() => buffs.UnitPiles(dummy, 16316) == 0, TimeSpan.FromSeconds(8)));
        Assert.True(SpinWait.SpinUntil(() => Hits() > channel, Wait));
    }
}
