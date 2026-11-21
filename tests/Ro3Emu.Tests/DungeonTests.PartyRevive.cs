using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private const int PriestJob = 4200, ResurrectionGroup = 14112, Resurrection = 1411201;

    // Phantom Realm (3120401, NoRevive): the dead player cannot revive themselves, but a Priest's Resurrection works while
    // the party's count (the boss's _iOriginalReviveNum) lasts; then ErrorCodeConfig 570.
    [ClientDataFact]
    public async Task APriestRevivesATeammateInAPhantomRealmUntilThePartyCountRunsOut()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        await Gm(alice, $"!job {PriestJob}");
        await OnLoop(async () =>
        {
            var priest = Session(alice);
            using var held = await CharacterService.Lock(priest);
            priest.Progress!.Skills[ResurrectionGroup] = 1;
        });
        Session(alice).GmCombat |= GmCombatSwitches.NoCooldown; // the second cast follows at once
        await Party(alice, bob, carol);
        Assert.Equal(0, await Enter(alice));
        foreach (var member in new[] { bob, carol })
        {
            await member.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(member, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        foreach (var member in new[] { alice, bob, carol }) await Load(member);
        for (var until = DateTime.UtcNow + TimeSpan.FromSeconds(10); Session(bob).DungeonAttempt is not { Started: true }; await Task.Delay(50))
            Assert.True(DateTime.UtcNow < until, "the run did not start");

        await KillBob();
        Assert.Equal(ErrorCodes.FeatureUnavailable, await ReliveSelf(bob)); // NoRevive: no revive of his own
        Assert.Equal(0, await CastResurrection(alice, bob));
        await Next(bob, WorldCoreService.ReliveInvitation, EntityReliveReceiveInviteNotify.Parser);
        await bob.SendGameAsync(WorldCoreService.ReliveAccept,
            new EntityReliveInviteAcceptReq { SourceEntityID = (long)alice.Self!.Uuid, IsAccept = true });
        Assert.Equal(0, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        var shown = await Next(carol, DungeonService.Event, DungeonEventNotify.Parser, n => n.Vars.Any(v => v.VarId == 140));
        int count = 0;
        await OnLoop(() =>
        {
            var attempt = Session(bob).DungeonAttempt!;
            Assert.False(Session(bob).Dead);
            Assert.Equal(1, attempt.RevivesUsed.Values.Sum());
            count = (int)new TenGameplayBossConfig(attempt.Plan.Boss!.Value).OriginalReviveNum;
            attempt.RevivesUsed[Session(carol).Role!.Id] = count - 1; // the rest spent on Carol: the count is shared
            return Task.CompletedTask;
        });
        Assert.True(count > 1);
        Assert.Equal(count - 1, shown.Vars.Single(v => v.VarId == 140).Vi64.Single()); // the HUD's "Revives Remaining"

        await KillBob();
        int refused = ErrorCodes.SkillBusy;
        for (var until = DateTime.UtcNow + TimeSpan.FromSeconds(10); refused == ErrorCodes.SkillBusy && DateTime.UtcNow < until; await Task.Delay(200))
            refused = await CastResurrection(alice, bob); // the first cast's action has to end
        Assert.Equal(ErrorCodes.PartyRevivesUsedUp, refused);
        await OnLoop(() => { Assert.True(Session(bob).Dead); return Task.CompletedTask; });

        async Task KillBob()
        {
            await OnLoop(() => Service<CombatService>().KillPlayerAsync(Session(bob)));
            await Next(bob, DungeonService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, info => info.BDead);
        }
    }

    private static async Task<int> CastResurrection(BotClient priest, BotClient target)
    {
        await priest.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = Resurrection, TargetId = (long)target.Self!.Uuid });
        return (await Next(priest, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
    }

    private static async Task<int> ReliveSelf(BotClient bot)
    {
        await bot.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        return (await Next(bot, CombatService.Relive, EntityReliveAck.Parser)).Result;
    }
}
