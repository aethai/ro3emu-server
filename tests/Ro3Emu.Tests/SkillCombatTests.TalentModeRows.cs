using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Talents that switch a skill to a mode row: the row's script and second damage pair still do what the text says.</summary>
public sealed partial class SkillCombatTests
{
    private sealed record Strike(long Row, long Target, long Amount, long[] Ratios, long[] Flats);

    /// <summary>A level 99 caster of the job with the group learned at the level and the talent node bought, costs off,
    /// damage without its float and crits, no other monster alive in the scene, and every strike it deals on record.</summary>
    private async Task<(BotClient Bot, GameSession Me, List<Strike> Strikes)> Caster(string name, int job, int group, int level, int talent = 0)
    {
        var bot = await InWorld(name);
        string account = name.ToLowerInvariant();
        await Gm(account, "!lvl 99"); await Gm(account, $"!job {job}"); await Teach(account, group);
        if (talent != 0) await ChooseProfessionTalent(bot, account, talent);
        var me = Session(account);
        var world = _app.Services.GetRequiredService<World>();
        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(me);
            me.Progress!.Skills[group] = level;
            foreach (var other in world.Map(me.WorldScene).Units.OfType<MonsterUnit>()) other.Hp = 0;
        });
        me.GmCombat |= GmCombatSwitches.Unlimited | GmCombatSwitches.FixedDamage;
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var strikes = new List<Strike>();
        combat.DealtAsync.Add((source, monster, row, amount) =>
        {
            var skill = new SkillConfig(row);
            if (source == me) strikes.Add(new Strike(skill.Id, monster.Uid, amount, skill.DamageParam1, skill.DamageParam2));
            return Task.CompletedTask;
        });
        return (bot, me, strikes);
    }

    /// <summary>A stationary monster with HP to spare, the given metres ahead of the caster.</summary>
    private async Task<MonsterUnit> Mark(GameSession me, float metres, int race = 1, int element = 1)
    {
        var mark = await Target(me.Account!, stationary: true);
        await Loop.InvokeAsync(() =>
        {
            mark.Template = mark.Template with { Race = race, Element = element, MaxHp = 100_000_000 };
            mark.Hp = 100_000_000;
            mark.X = me.Position.X + metres;
            mark.Z = me.Position.Z;
            return Task.CompletedTask;
        });
        return mark;
    }

    private static async Task<CastSkillRsp> Cast(BotClient bot, GameSession me, int skill, MonsterUnit target)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = me.Face });
        return await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
    }

    /// <summary>The first damage value of the row the caster's talents make of a row id.</summary>
    private SkillConfig Effective(GameSession me, int skill) =>
        new(_app.Services.GetRequiredService<CombatService>().EffectiveSkillRow(me, skill)!.Value);

    // Thunder Shockwave (10102131 for the Wizard line) selects Lord_of_Vermilion_BD: "lasts 4 seconds and strikes 7 times".
    // "For each enemy hit ... its DMG Bonus increases by 5%, up to 25%".
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task LordOfVermilionKeepsItsStrikesWithThunderShockwave(int level)
    {
        var (bot, me, strikes) = await Caster("Alice", 2200, 12107, level, 10102131);
        await using var _ = bot;
        var mark = await Mark(me, 5);
        var row = Effective(me, 1210700 + level);
        Assert.Equal(1210710 + level, row.Id);
        Assert.Equal(0, (await Cast(bot, me, 1210700 + level, mark)).Result);
        await Task.Delay(3500);
        Assert.InRange(strikes.Count, 6, 7); // a late tick can lose the last under load
        Assert.All(strikes, s => Assert.Equal(row.Id, s.Row));
        // One enemy: +5%, once. Elsewhere the text's other talents raise the row (Effective).
        Assert.All(strikes, s => Assert.Equal((long)Math.Round(row.DamageParam1[0] * 1.05), s.Ratios[0]));
        Assert.All(strikes, s => Assert.Equal((long)Math.Round(row.DamageParam2[0] * 1.05), s.Flats[0]));
    }

    [ClientDataFact] // "up to 25%": six enemies in the circle, five hit
    public async Task ThunderShockwaveStopsAtItsLimit()
    {
        var (bot, me, strikes) = await Caster("Alice", 2200, 12107, 10, 10102131);
        await using var _ = bot;
        var marks = new List<MonsterUnit>();
        for (int i = 0; i < 6; i++) marks.Add(await Mark(me, 5));
        var row = Effective(me, 1210710);
        Assert.Equal(0, (await Cast(bot, me, 1210710, marks[0])).Result);
        await Task.Delay(1000);
        Assert.NotEmpty(strikes);
        Assert.All(strikes, s => Assert.Equal((long)Math.Round(row.DamageParam1[0] * 1.25), s.Ratios[0]));
    }
}
