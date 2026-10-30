using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Same Old Trick (Rainbow Ferus 20001, PetStarConfig._iPassiveMain 2337201): "When an Offensive-type or Control-type
    // Primary Battle Skill Deals DMG, it deals ${1} Extra hits of Fire melee Adaptive Damage" from a support slot. Its
    // fire (2337501, Deleter_Passive) shows through skill_23375, whose selector picks nobody: it must still land.
    [ClientDataFact]
    public async Task GuardianFerusInASupportSlotAddsFireHitsToThePrimaryBattleSkill()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 20004, 1620004, pieces: 40);
        await EquipGuardian(alice, 20001, 1620001, slot: 2, pieces: 40);
        var target = await SupportProcTarget();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2347301, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId / 100 == 23474);
        var fire = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId / 100 == GuardianGroup.RainbowFire);
        Assert.Contains(fire.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    // Stunning Tremor (20004, _iPassiveMain 2347201): "When an Offensive-type Primary Battle Skill deals damage, ${1}%
    // chance to inflict [Stun] for ${2} sec" from a support slot. Rainbow Ferus's breath hits five times.
    [ClientDataFact]
    public async Task GuardianStunningTremorInASupportSlotStunsWithThePrimaryBattleSkill()
    {
        await using var alice = await InWorld("Alice");
        await EquipGuardian(alice, 20001, 1620001, pieces: 40);
        await EquipGuardian(alice, 20004, 1620004, slot: 2, pieces: 40);
        await Loop.InvokeAsync(() => // GmNoCD / GmUnlimit: recast the same skill without its cooldown or cost
        {
            Session("alice").GmCombat |= GmCombatSwitches.NoCooldown | GmCombatSwitches.Unlimited;
            return Task.CompletedTask;
        });
        bool stunned = false;
        for (int attempt = 0; attempt < 4 && !stunned; attempt++)
        {
            var target = await SupportProcTarget();
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2337301, TargetId = target.Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId / 100 == 23374);
            await Task.Delay(2500);
            stunned = alice.Inbox.Snapshot().Any(e => e.IsGame(BuffService.BuffUpdate)
                && BuffUpdateNotify.Parser.ParseFrom(e.Body) is { } b && b.UnitId == target.Uid
                && b.Buffs.Any(x => x.ConfigId == GuardianPetRules.StunBuff));
        }
        Assert.True(stunned); // 25% per hit, five hits a cast: four casts all missing is about 1 in 300 000
    }

    private async Task<MonsterUnit> SupportProcTarget()
    {
        var s = Session("alice");
        return await Loop.InvokeAsync(async () =>
        {
            var monsters = _app.Services.GetRequiredService<MonsterService>();
            var template = monsters.Template(5000001) ?? throw new InvalidOperationException("Missing exported monster");
            var unit = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
                new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId,
                customTemplate: template with { RoamDistance = 0, SpeedCm = 0 }) ?? throw new InvalidOperationException("Cannot create test target");
            unit.Hp = 10_000_000;
            return unit;
        });
    }
}
