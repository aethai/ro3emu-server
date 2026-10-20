using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task EquipGuardian(BotClient bot, int petId, int fragment)
    {
        await Gm("alice", "!quest done 10104330");
        await Gm("alice", "!lvl 50");
        await Gm("alice", $"!item {fragment} 20");
        await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = petId });
        Assert.Equal(0, (await Next(bot, PetService.Combine, PetCombineResp.Parser)).Result);
        await bot.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq
            { PetId = petId, Target = (PetStateSource_Type)1, TargetSlotIndex = 1 });
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task GuardianChocoAutomaticallyBuffsOwnerAndSharesCooldownWithManualCast()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 10001, 1610001);
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        long speed = Character.Stats(s)[114];
        int normal = app.Services.GetRequiredService<SkillService>().NormalAttack(s.Progress!.Career);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var petCast = await Next(alice, CombatService.ExtraUseInSkill, UseInSkillInfo.Parser, p => p.PetConfigId == 10001);
        Assert.Equal(2353401, petCast.SkillID); // child type 41, not the empty wrapper or a tower skill
        Assert.Equal(s.Role!.Id, petCast.SrcID);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2353401));
        Assert.Contains(s.Buffs, b => b.ConfigId == 2353401 && b.Changes.Any(c => c.Attr == 114 && c.Value == 10));
        Assert.True(Character.Stats(s)[114] > speed);
        Assert.True(s.Attack != null || s.SkillCast?.Request.SkillId == normal);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2353301, TargetId = s.Role.Id });
        Assert.Equal(ErrorCodes.SkillCooldown, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task GuardianRockerHealsNearbyPlayersWithoutDamagingThePresentationTarget()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await EquipGuardian(alice, 11001, 1611001);
        var s = Session("alice");
        var ally = Session("bob");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 100000;
            await Character.SetHpSpAsync(s, 1, null);
            await Character.SetHpSpAsync(ally, 1, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2356101);
        Assert.Contains(hit.Dsts, d => d.DstID == s.Role!.Id && d.Damage > 0 && d.DamageType == DamageType.Heal);
        Assert.Contains(hit.Dsts, d => d.DstID == ally.Role!.Id && d.Damage > 0 && d.DamageType == DamageType.Heal);
        Assert.DoesNotContain(hit.Dsts, d => d.DstID == target.Uid);
        Assert.Equal(100000, target.Hp);
    }

    [ClientDataFact]
    public async Task GuardianWildRoseDamagesThroughSharedCombatAndRejectsUnequippedOrChildCasts()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 12001, 1612001);
        var target = await Target("alice", stationary: true);
        var s = Session("alice");
        await Loop.InvokeAsync(() => { target.Hp = 100000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2355101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2355201);
        Assert.Contains(hit.Dsts, d => d.DstID == target.Uid && d.Damage > 0 && d.DamageType != DamageType.Heal);
        Assert.True(target.Hp < 100000);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2355201, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq
            { PetId = 12001, Source = (PetStateSource_Type)1, SourceSlotIndex = 1 });
        Assert.Equal(0, (await Next(alice, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2355101, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task GuardianBoboGrantsFiniteShieldAndDeployedPassiveStatsAreRemovedOnUnequip()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 50");
        await Gm("alice", "!quest done 10104330");
        var s = Session("alice");
        long beforeAttack = Character.Stats(s)[103], beforeLife = Character.Stats(s)[101];
        await EquipGuardian(alice, 13001, 1613001);
        Assert.True(Character.Stats(s)[103] > beforeAttack || Character.Stats(s)[101] > beforeLife);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2354001, TargetId = s.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == 2354101));
        Assert.True(Assert.Single(s.Buffs, b => b.ConfigId == 2354101).ShieldRemaining > 0);
        await alice.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq
            { PetId = 13001, Source = (PetStateSource_Type)1, SourceSlotIndex = 1 });
        Assert.Equal(0, (await Next(alice, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        Assert.Equal(beforeAttack, Character.Stats(s)[103]);
        Assert.Equal(beforeLife, Character.Stats(s)[101]);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardianPendingDamageCannotSurvivePlanChangeOrSceneGenerationChange(bool changeScene)
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 12001, 1612001);
        var target = await Target("alice", stationary: true);
        var s = Session("alice");
        var combat = app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 100000;
            await combat.CastAsync(s, new CastSkillReq { SkillId = 2355101, TargetId = target.Uid });
            // Invalidate each source independently before the scheduled hit; no time-dependent wire race.
            if (changeScene) s.SceneGeneration++;
            else s.Pets!.CurrentPlan = 1;
        });
        await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.Result == 0);
        await Task.Delay(1500);
        Assert.Equal(100000, target.Hp);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 2355201);
    }
}
