using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Switching skill builds over invented data: a saved plan that still holds an earlier career's free skill (a GM job
/// change into another line), and a recommended build chosen with 31160 (JobProfessConfig, JobProfessSkillConfig) that
/// costs more points than the job level has earned.
/// </summary>
public sealed class SkillBuildSwitchTests
{
    private const int Base = 981, NeedsBase = 982, Strike = 983, OldFree = 984;
    private const int FirstCareer = 1000, OtherCareer = 2000, Build = 70;

    [Fact]
    public async Task APlanHoldingAnEarlierCareersFreeSkillSwitchesInAndResets() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(1, Stored(session, 1, OldFree)); // every plan lists the first career's free skill at its free level
        await host.Service<GameLoop>().InvokeAsync(() => host.Service<CharacterService>().SetJobAsync(session, OtherCareer));

        await alice.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 1 });
        Assert.Equal(0, (await Next(alice, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
        Assert.Equal(1, session.Combat.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(0, skills.Level(session.Progress!, OldFree)); // in no tree of the new career: no level

        await alice.SendGameAsync(SkillService.SkillReset, new SkillResetReq { Plan = 2 });
        Assert.Equal(0, (await Next(alice, SkillService.SkillReset, SkillResetResp.Parser)).Result);
    });

    [Fact]
    public async Task AnEarlierCareersPaidLevelStillKeepsAPlanOut() => await InWorld(async (host, alice, session) =>
    {
        session.Combat.SkillPlans!.SkillAssignPlans[1].SkillAssigns[Strike] = new SkillData { SkillId = Strike, SkillLevel = 1 };
        await host.Service<GameLoop>().InvokeAsync(() => host.Service<CharacterService>().SetJobAsync(session, OtherCareer));

        await alice.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 1 });
        Assert.Equal(ErrorCodes.SkillNotInTree, (await Next(alice, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
        Assert.Equal(0, session.Combat.SkillPlans!.CurrentPlanIndex);
    });

    // The build lists 982 (needs 981 at 1), 983 at 2 and 981 at 2: five points where the job level earned three. Its last
    // skill loses a level, then 983, because 982 needs 981's last level.
    [Fact]
    public async Task ChoosingABuildAllocatesItWithinTheEarnedPoints() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(0, await SwitchGenre(alice, Build));

        var plan = skills.Build(session).SkillAssignPlans[0];
        Assert.Equal(Build, plan.SkillGenre);
        Assert.Equal(1, plan.SkillAssigns[NeedsBase].SkillLevel);
        Assert.Equal(1, plan.SkillAssigns[Strike].SkillLevel);
        Assert.Equal(1, plan.SkillAssigns[Base].SkillLevel);
        Assert.Equal(0, plan.SkillPoints[1]);
        Assert.Equal(1, skills.Level(session.Progress!, Strike));
        Assert.Equal(3, skills.SpentPoints(session.Progress!));
    });

    [Fact]
    public async Task AllocatingByHandCancelsTheChosenBuild() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(0, await SwitchGenre(alice, Build));
        Assert.Equal(0, await SwitchGenre(alice, Build)); // the build the plan already has: nothing is allocated again
        Assert.Equal(3, skills.SpentPoints(session.Progress!));

        var refund = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        refund.LevelUpList[Strike] = new SkillData { SkillId = Strike, SkillLevel = 0 };
        await alice.SendGameAsync(SkillService.PlanUpdate, refund);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        Assert.Equal(0, skills.Build(session).SkillAssignPlans[0].SkillGenre);
        Assert.Equal(2, skills.SpentPoints(session.Progress!));

        Assert.Equal(0, await SwitchGenre(alice, Build)); // chosen again: allocated again
        Assert.Equal(3, skills.SpentPoints(session.Progress!));
    });

    [Fact]
    public async Task AGenreWithoutABuildOnlyBindsThePlan() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(0, await SwitchGenre(alice, 5));
        Assert.Equal(5, skills.Build(session).SkillAssignPlans[0].SkillGenre);
        Assert.Equal(0, skills.SpentPoints(session.Progress!));
    });

    private static int Stored(GameSession session, int plan, int group) =>
        session.Combat.SkillPlans!.SkillAssignPlans[plan].SkillAssigns.GetValueOrDefault(group)?.SkillLevel ?? 0;

    private static async Task<int> SwitchGenre(BotClient bot, int genre)
    {
        await bot.SendGameAsync(SkillService.SwitchGenre, new SkillSwitchGenreReq { Genre = genre });
        return (await Next(bot, SkillService.SwitchGenre, SkillSwitchGenreResp.Parser)).Result;
    }

    private static async Task InWorld(Func<SyntheticHost, BotClient, GameSession, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-buildswitch-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            await test(host, alice, host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        var message = await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(5));
        return parser.ParseFrom(message.Body);
    }

    private static void PrepareData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Write(root, "JobConfig", $$$"""
            {"{{{FirstCareer}}}":{"_iID":{{{FirstCareer}}},"_iJobRank":1},"{{{OtherCareer}}}":{"_iID":{{{OtherCareer}}},"_iJobRank":1}}
            """);
        Write(root, "JobLvConfig", """
            {"101":{"_iID":101,"_iSkillPoint":3}}
            """);
        Write(root, "SkillConfig", """
            {
              "98101":{"_iID":98101,"_iSkillID":981,"_iLevel":1,"_iMaxLevel":2,"_iMode":0,"_iBasicType":1},
              "98102":{"_iID":98102,"_iSkillID":981,"_iLevel":2,"_iMaxLevel":2,"_iMode":0,"_iBasicType":1},
              "98201":{"_iID":98201,"_iSkillID":982,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1,
                "_kPreSkillRequired":[[981,1]]},
              "98301":{"_iID":98301,"_iSkillID":983,"_iLevel":1,"_iMaxLevel":2,"_iMode":0,"_iBasicType":1},
              "98302":{"_iID":98302,"_iSkillID":983,"_iLevel":2,"_iMaxLevel":2,"_iMode":0,"_iBasicType":1},
              "98401":{"_iID":98401,"_iSkillID":984,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iInitialLevel":1},
              "98501":{"_iID":98501,"_iSkillID":985,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1}
            }
            """);
        Write(root, "SkillTreeConfig", $$$"""
            {"1":{"_iID":1,"_iJob":{{{FirstCareer}}},"_iX1":981,"_iX2":982,"_iX3":983,"_iX4":984},
             "2":{"_iID":2,"_iJob":{{{OtherCareer}}},"_iX1":985}}
            """);
        Write(root, "JobProfessConfig", $$$"""
            {"{{{Build}}}":{"_iID":{{{Build}}},"_iProfessionID":7}}
            """);
        Write(root, "JobProfessSkillConfig", """
            {"7101":{"_iID":7101,"_iJobLv":1,"_iJobRank":1,"_iProfessionID":7,"_kDescData":[98201,98302,98102]}}
            """);
    }

    private static void Write(string root, string table, string json) =>
        File.WriteAllText(Path.Combine(root, "config", table + ".json"), json);
}
