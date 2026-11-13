using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int FlameLauncherGroup = 16315, FlameLauncherRow = 1631501, ArmCannonGroup = 16312, CannonEnhance = 1632001, FirepowerLoaded = 1631701;

    private async Task CastAndWait(Ro3Emu.Bot.BotClient bot, int skill, Ro3Emu.Server.Maps.MonsterUnit target)
    {
        await Loop.InvokeAsync(() => { Session("alice").SkillCds.Clear(); Session("alice").SkillModeCds.Clear(); return Task.CompletedTask; });
        await CastAtAsync(bot, skill, target);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == target.Uid));
        await Task.Delay(800); // the swing has played out
    }

    [ClientDataFact] // "After casting this skill 2 times, the next Warg Strike becomes Empowered"
    public async Task TwoWargStrikesEmpowerTheNext()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 3400, 10032, 1003201);
        await CastAndWait(bot, 1003201, dummy);
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1333101);
        await CastAndWait(bot, 1003201, dummy);
        Assert.Contains(a.Buffs, b => b.ConfigId == 1333101);
    }

    [ClientDataFact] // Cross Ripper Slasher: blades times its ratio; the full four are spent and reset its cooldown
    public async Task CrossRipperSpendsItsBlades()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 5400, 15302, 1530201);
        await Loop.InvokeAsync(() => { a.Energy[540002].Value = 4; return Task.CompletedTask; });
        await CastAndWait(bot, 1530201, dummy);
        Assert.Equal(0, a.Energy[540002].Value);
        Assert.False(a.SkillCds.ContainsKey(15302));
    }

    [ClientDataFact] // Vulcan Arm: "Every 15 casts": one cast loads nothing
    public async Task VulcanArmLoadsFirepowerOnlyAtItsPiles()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16313, 1631301);
        await CastAndWait(bot, 1631301, dummy);
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1631701);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () => { for (int i = 0; i < 14; i++) await buffs.AddAsync(a, 1631301, 15000, a.Role!.Id); });
        await CastAndWait(bot, 1631301, dummy);
        var deadline = DateTime.UtcNow + Wait;
        while (!a.Buffs.Any(b => b.ConfigId == 1632001) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(3, a.Buffs.Where(b => b.ConfigId == 1631701).Sum(b => b.PileCount));
        Assert.Contains(a.Buffs, b => b.ConfigId == 1632001);
    }

    [ClientDataFact] // Energy Release: countdown, then the blast from its second row, then half the Max HP lost
    public async Task EnergyReleaseGoesOffAfterItsCountdown()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16317, 1631701);
        await CastAtAsync(bot, 1631701, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1000);
        Assert.Equal(1000000, dummy.Hp); // still counting down
        Assert.Contains(a.Buffs, b => b.ConfigId == 1635101);
        var blast = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0));
        Assert.Equal(1635201, blast.SkillId);
    }

    [ClientDataFact] // Precision Thrust: a full counter turns into a Hundred Spears charge
    public async Task PrecisionThrustTurnsItsCounterIntoACharge()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1400, 11315, 1131501);
        await Loop.InvokeAsync(() => { a.Energy[140001].Value = 5; a.Energy[140002].Value = 0; a.Energy[140002].NextRecoverMs = MonsterService.NowMs + 60000; return Task.CompletedTask; });
        await CastAndWait(bot, 1131501, dummy);
        Assert.Equal(0, a.Energy[140001].Value);
        Assert.Equal(1, a.Energy[140002].Value);
    }

    [ClientDataFact] // Flame Launcher: Cannon Enhance switches Arm Cannon's mode until spent, so its slot counts nothing down and the buffs carry no time
    public async Task FlameLauncherEnhancesArmCannonWithNoCountdown()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, FlameLauncherGroup, FlameLauncherRow);
        await Teach("alice", ArmCannonGroup);
        await Loop.InvokeAsync(() => { a.Progress!.SkillBar.Clear(); a.Progress.SkillBar[1] = ArmCannonGroup; return Task.CompletedTask; });
        await CastAndWait(bot, FlameLauncherRow, dummy);
        var deadline = DateTime.UtcNow + Wait;
        while (!a.Buffs.Any(b => b.ConfigId == CannonEnhance) && DateTime.UtcNow < deadline) await Task.Delay(50);

        var slot = (await ShownSkills("alice")).SkillWheel.WhellSkills[1];
        Assert.NotEqual(0, slot.Mode);
        Assert.Equal(0L, slot.HighlightEndTimeMs);
        foreach (int buff in new[] { CannonEnhance, FirepowerLoaded })
        {
            var sent = bot.Inbox.Snapshot().Where(e => e.IsGame(BuffService.BuffUpdate))
                .SelectMany(e => BuffUpdateNotify.Parser.ParseFrom(e.Body).Buffs).Last(b => b.ConfigId == buff);
            Assert.Equal(0L, sent.ExpireTime);
            Assert.Equal(0L, sent.Duration);
        }
        Assert.Equal(3, a.Buffs.Single(b => b.ConfigId == FirepowerLoaded).PileCount);
    }
}
