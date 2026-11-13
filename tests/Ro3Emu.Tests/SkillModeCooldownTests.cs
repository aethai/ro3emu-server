using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A buff that selects a skill mode (BuffConfig type 15, as Unlimited Shooting does for Focused Arrow Strike), over
/// invented data: the 31113 entries filed under the mode's key when it starts and under the plain key when it ends carry
/// the cooldown the server still holds (CombatService.CooldownEnd), so the client's bar never loses a running one.
/// </summary>
public sealed class SkillModeCooldownTests
{
    private const int Group = 990, PlainRow = 99001, ModeRow = 99011, ModeBuff = 99001, Mode = 1;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AModeStartsAndEndsWithTheRunningCooldown() => await InWorld(async (host, alice, session) =>
    {
        long end = await host.Service<GameLoop>().InvokeAsync(() =>
        {
            session.SkillCds[Group] = MonsterService.NowMs + 60_000;
            return Task.FromResult(session.SkillCds[Group]);
        });
        var buffs = host.Service<BuffService>();
        await host.Service<GameLoop>().InvokeAsync(() => buffs.AddAsync(session, ModeBuff, 10_000, session.Role!.Id));
        var started = await Next(alice, m => m.Infos.Any(i => i.SkillId == Group && i.SkillMode == Mode));
        Assert.Equal(end, started.Infos.Single(i => i.SkillId == Group).CdEndTimestampMs);

        await host.Service<GameLoop>().InvokeAsync(() => buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == ModeBuff)));
        var ended = await Next(alice, m => m.Infos.Any(i => i.SkillId == Group && i.SkillMode == 0));
        Assert.Equal(end, ended.Infos.Single(i => i.SkillMode == 0).CdEndTimestampMs);
        var closed = ended.Infos.Single(i => i.SkillMode == Mode);
        Assert.Equal(closed.CdStartTimestampMs, closed.CdEndTimestampMs);
    });

    [Fact]
    public async Task AModeEndingWithoutACooldownLeavesThePlainKeyAlone() => await InWorld(async (host, alice, session) =>
    {
        var buffs = host.Service<BuffService>();
        await host.Service<GameLoop>().InvokeAsync(() => buffs.AddAsync(session, ModeBuff, 10_000, session.Role!.Id));
        var started = await Next(alice, m => m.Infos.Any(i => i.SkillId == Group && i.SkillMode == Mode));
        Assert.Equal(0, started.Infos.Single(i => i.SkillId == Group).CdEndTimestampMs);

        await host.Service<GameLoop>().InvokeAsync(() => buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == ModeBuff)));
        var ended = await Next(alice, m => m.Infos.Any(i => i.SkillId == Group && i.SkillMode == Mode));
        Assert.DoesNotContain(ended.Infos, i => i.SkillMode == 0);
    });

    private static async Task<SkillCdNotify> Next(BotClient bot, Func<SkillCdNotify, bool> match) =>
        SkillCdNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(
            e => e.IsGame(CombatService.SkillCdUpdate) && match(SkillCdNotify.Parser.ParseFrom(e.Body)), Wait)).Body);

    private static async Task InWorld(Func<SyntheticHost, BotClient, GameSession, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-modecd-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
            await host.Service<GameLoop>().InvokeAsync(() => { session.Progress!.Skills[Group] = 1; return Task.CompletedTask; });
            await test(host, alice, session);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void PrepareData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), $$"""
            {
              "{{PlainRow}}":{"_iID":{{PlainRow}},"_iSkillID":{{Group}},"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1},
              "{{ModeRow}}":{"_iID":{{ModeRow}},"_iSkillID":{{Group}},"_iLevel":1,"_iMaxLevel":1,"_iMode":{{Mode}},"_iBasicType":1}
            }
            """);
        File.WriteAllText(Path.Combine(root, "config", "BuffConfig.json"), $$"""
            {
              "{{ModeBuff}}":{"_iID":{{ModeBuff}},"_iBuffID":990,"_kSkillAttributeList":[[0,{{Group}},15,2,{{Mode}}]] }
            }
            """);
    }
}
