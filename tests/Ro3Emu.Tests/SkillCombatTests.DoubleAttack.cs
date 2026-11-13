using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Double Attack (passive group 15002): its hit is its own row 1511501 (group 15115), whose damage the client adds to the
/// dagger Basic Attack's running total: that attack's _iAccumDmgType [4, 1000, 15115] (LC_DamageHelper._getAccumDmgValue).
/// </summary>
public sealed partial class SkillCombatTests
{
    private const int DoubleAttackPassive = 15002, DoubleAttackHitRow = 1511501, DoubleAttackHitGroup = 15115;

    [ClientDataFact]
    public async Task DoubleAttackHitsAsItsOwnRowWhichTheBasicAttacksTotalCollects()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5100"); await Teach("alice", DoubleAttackPassive);
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            using (await CharacterService.Lock(s)) s.Progress!.Skills[DoubleAttackPassive] = 10;
        });
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });
        await HoldInMainHand("alice", Dagger);
        int attack = _app.Services.GetRequiredService<SkillService>().CurrentAttack(Session("alice"));
        var accum = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), attack)!.Value.Row.GetProperty("_iAccumDmgType");
        Assert.Equal([4, 1000, DoubleAttackHitGroup], accum.EnumerateArray().Select(v => v.GetInt32()));

        RollAtChance(0);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attack, TargetId = monster.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var proc = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == DoubleAttackHitRow);
        Assert.Contains(proc.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }
}
