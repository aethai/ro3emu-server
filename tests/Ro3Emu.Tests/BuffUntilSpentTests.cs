using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented content: a buff that lasts until it is spent reaches the client with no time, and the skill mode it
/// selects shows no countdown on the wheel; a timed buff keeps its end in both.</summary>
public sealed class BuffUntilSpentTests
{
    // Every id and value below is invented.
    private const int SpentGroup = 881, TimedGroup = 882, SpentBuff = 88001, TimedBuff = 88002, TimedMs = 5000;

    private static object Row(int group, int mode) => new
    {
        _iID = group * 100 + mode * 10 + 1, _iSkillID = group, _iLevel = 1, _iMaxLevel = 1, _iMode = mode, _iBasicType = 1,
        _kScriptLogic = "Synthetic", _iScriptShow = group * 10,
    };

    private static object Buff(int id, int buffGroup, int time, int skillGroup) => new
    {
        _iID = id, _iBuffID = buffGroup, _iTime = time,
        _kSkillAttributeList = new[] { new[] { "0", $"{skillGroup}", "15", "1", "1" } }, // [scope, group, type 15 mode, op, value]
    };

    private static void PrepareData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), JsonSerializer.Serialize(value));
        }
        var skills = new Dictionary<string, object>();
        foreach (int group in new[] { SpentGroup, TimedGroup })
            foreach (int mode in new[] { 0, 1 }) skills[$"{group * 100 + mode * 10 + 1}"] = Row(group, mode);
        Write("config/SkillConfig.json", skills);
        Write("config/BuffConfig.json", new Dictionary<string, object>
        {
            [$"{SpentBuff}"] = Buff(SpentBuff, 880, 0, SpentGroup), [$"{TimedBuff}"] = Buff(TimedBuff, 870, TimedMs, TimedGroup),
        });
    }

    [Fact]
    public async Task AnUntilSpentBuffSendsNoTimeAndItsModeNoCountdown()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-untilspent-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            var a = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
            var buffs = host.Service<BuffService>();
            var shown = await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                using (await CharacterService.Lock(a))
                {
                    a.Progress!.Skills[SpentGroup] = 1;
                    a.Progress.Skills[TimedGroup] = 1;
                    a.Progress.SkillBar.Clear();
                    a.Progress.SkillBar[1] = SpentGroup;
                    a.Progress.SkillBar[2] = TimedGroup;
                }
                await buffs.AddUntilSpentAsync(a, SpentBuff, a.Role!.Id);
                await buffs.AddAsync(a, TimedBuff, 0, a.Role.Id);
                return host.Service<SkillService>().Shown(a).SkillWheel.WhellSkills;
            });
            long timedEnd = a.Buffs.Single(b => b.ConfigId == TimedBuff).ExpireMs;

            Assert.Equal(SpentGroup * 100 + 11, shown[1].SkillConfigId); // the mode 1 row
            Assert.Equal(1, shown[1].Mode);
            Assert.Equal(0L, shown[1].HighlightEndTimeMs);
            Assert.Equal(TimedGroup * 100 + 11, shown[2].SkillConfigId);
            Assert.Equal(timedEnd, shown[2].HighlightEndTimeMs);

            await alice.Inbox.WaitAsync(e => e.IsGame(BuffService.BuffUpdate) && BuffUpdateNotify.Parser.ParseFrom(e.Body).Buffs.Any(b => b.ConfigId == TimedBuff),
                TimeSpan.FromSeconds(5));
            var sent = alice.Inbox.Snapshot().Where(e => e.IsGame(BuffService.BuffUpdate))
                .SelectMany(e => BuffUpdateNotify.Parser.ParseFrom(e.Body).Buffs).ToArray();
            var spent = sent.Last(b => b.ConfigId == SpentBuff);
            Assert.Equal((0L, 0L), (spent.ExpireTime, (long)spent.Duration));
            var timed = sent.Last(b => b.ConfigId == TimedBuff);
            Assert.Equal((timedEnd, (long)TimedMs), (timed.ExpireTime, (long)timed.Duration));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
