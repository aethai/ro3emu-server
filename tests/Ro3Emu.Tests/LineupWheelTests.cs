using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented jobs and skills exercise the client's dedicated Lineup slot without client data.</summary>
public sealed class LineupWheelTests
{
    private const int QueueGroup = 991, QueueRow = 99101, BuffGroup = 992;
    private const int QueueSlot = (int)SkillWheelSlotType.SkillWheelSkillQueue;

    [Fact]
    public async Task QueueSlotAppearsOverWireSurvivesRestartAndClearsWhenUnloaded()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-lineup-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using (var alice = await host.InWorldAsync("Alice"))
            {
                var fresh = await Next<DBSkillDataIncrement>(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
                Assert.Contains(QueueGroup, fresh.SkillList.All.Value);
                Assert.False(fresh.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
                await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad);
                var loaded = await Next<DBSkillDataIncrement>(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
                Assert.Equal(QueueRow, loaded.SkillWheel.Inc.WhellSkills[QueueSlot].All.SkillConfigId);
                Assert.True(loaded.SkillWheel.Inc.WhellSkills[QueueSlot].All.Enable);
                Assert.Equal(QueueRow, loaded.OriginSkillWheel.Inc.WhellSkills[QueueSlot].All.SkillConfigId);
            }
            await host.RestartAsync();
            await using var again = await host.InWorldAsync("Alice", create: false);
            var restored = await Next<DBSkillDataIncrement>(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.Equal(BuffGroup, restored.SkillAssignPlans.All.Value[0].SkillQueue[0].SkillId);
            Assert.Equal(QueueRow, restored.SkillWheel.All.WhellSkills[QueueSlot].SkillConfigId);
            Assert.Equal(QueueRow, restored.OriginSkillWheel.All.WhellSkills[QueueSlot].SkillConfigId);
            // A regular skill remains in its own slot; Lineup consumes none of the six manual positions.
            Assert.Equal(99201, restored.SkillWheel.All.WhellSkills[1].SkillConfigId);
            await UpdateQueue(again, SkillLoadOperation.SkillLoadOpUnload);
            var cleared = await Next<DBSkillDataIncrement>(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.True(cleared.SkillWheel.Inc.WhellSkills[QueueSlot].Del);
            Assert.True(cleared.OriginSkillWheel.Inc.WhellSkills[QueueSlot].Del);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task UpdateQueue(BotClient bot, SkillLoadOperation operation)
    {
        await bot.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
        {
            Plan = 0, QueueIndex = 0, OpType = operation,
            Info = new SkillData { SkillId = BuffGroup, SkillLevel = 1 }
        });
        Assert.Equal(0, (await Next<SkillQueueUpdateResp>(bot, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        var message = await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(8));
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
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """
            {"1000":{"_iID":1000,"_iJobRank":1,"_iQueueSkillId":99101}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """
            {
              "99101":{"_iID":99101,"_iSkillID":991,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iSystemType":6},
              "99201":{"_iID":99201,"_iSkillID":992,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,
                "_iBasicType":1,"_iInitialLevel":1,"_iQueue":1}
            }
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillTreeConfig.json"), """
            {"1":{"_iID":1,"_iJob":1000,"_iX1":992}}
            """);
    }
}
