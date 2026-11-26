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
            session.Combat.SkillCds[Group] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
            return Task.FromResult(session.Combat.SkillCds[Group]);
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

    [ClientDataFact]
    public async Task UnlimitedShootingSendsCooldownAndAffectedParamsBeforeSkillWheelSync()
    {
        const int focusedArrowGroup = 13305, focusedArrowBaseRow = 1330501, focusedArrowMode1Row = 1330511, unlimitedBuff = 1330201;
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data"));
        await using var alice = await host.InWorldAsync("Alice");
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var loop = host.Service<GameLoop>();
        var skills = host.Service<SkillService>();
        var buffs = host.Service<BuffService>();

        await loop.InvokeAsync(async () =>
        {
            session.Progress!.Skills[focusedArrowGroup] = 1;
            session.Progress.SkillBar[1] = focusedArrowGroup;
            session.Combat.SkillCds[focusedArrowGroup] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
            await skills.SyncAsync(session);
        });
        await alice.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync), Wait);

        int start = alice.Inbox.Snapshot().Count;
        using var cdStart = alice.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == focusedArrowGroup && i.SkillMode == 1));
        using var exStart = alice.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx));
        using var wheelStart = alice.Inbox.Expect(e => e.IsGame(SkillService.SkillDataSync));

        await loop.InvokeAsync(() => buffs.AddAsync(session, unlimitedBuff, 10_000, session.Role!.Id));
        await cdStart.WaitAsync(Wait);
        await exStart.WaitAsync(Wait);
        var syncOn = DBSkillDataIncrement.Parser.ParseFrom((await wheelStart.WaitAsync(Wait)).Body);

        var onEvents = alice.Inbox.Snapshot().Skip(start).ToArray();
        int cdIdx = Array.FindIndex(onEvents, e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == focusedArrowGroup && i.SkillMode == 1));
        int exIdx = Array.FindIndex(onEvents, e => e.IsGame(CombatService.SkillAffectedEx));
        int syncIdx = Array.FindIndex(onEvents, e => e.IsGame(SkillService.SkillDataSync));
        Assert.True(cdIdx >= 0 && exIdx >= 0 && syncIdx >= 0);
        Assert.True(cdIdx < syncIdx, $"Expected 31113 ({cdIdx}) before 5025 ({syncIdx}) on Unlimited Shooting start.");
        Assert.True(exIdx < syncIdx, $"Expected 31132 ({exIdx}) before 5025 ({syncIdx}) on Unlimited Shooting start.");
        Assert.Contains(syncOn.SkillWheel!.Inc.WhellSkills.Values,
            slot => (slot.Inc?.SkillConfigId ?? slot.All?.SkillConfigId) == focusedArrowMode1Row);

        int offStart = alice.Inbox.Snapshot().Count;
        using var cdEnd = alice.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == focusedArrowGroup && i.SkillMode == 0));
        using var wheelEnd = alice.Inbox.Expect(e => e.IsGame(SkillService.SkillDataSync));

        await loop.InvokeAsync(() => buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == unlimitedBuff)));
        await cdEnd.WaitAsync(Wait);
        var syncOff = DBSkillDataIncrement.Parser.ParseFrom((await wheelEnd.WaitAsync(Wait)).Body);

        var offEvents = alice.Inbox.Snapshot().Skip(offStart).ToArray();
        int cdOffIdx = Array.FindIndex(offEvents, e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == focusedArrowGroup && i.SkillMode == 0));
        int syncOffIdx = Array.FindIndex(offEvents, e => e.IsGame(SkillService.SkillDataSync));
        Assert.True(cdOffIdx >= 0 && syncOffIdx >= 0);
        Assert.True(cdOffIdx < syncOffIdx, $"Expected 31113 ({cdOffIdx}) before 5025 ({syncOffIdx}) on Unlimited Shooting end.");
        Assert.Contains(syncOff.SkillWheel!.Inc.WhellSkills.Values,
            slot => (slot.Inc?.SkillConfigId ?? slot.All?.SkillConfigId) == focusedArrowBaseRow);
    }

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
