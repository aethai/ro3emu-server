using Microsoft.Extensions.Time.Testing;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The memoized stat sheet (CharacterStatsService.Memo): a combat path reuses the build across work items while its
/// inputs are unchanged, and rebuilds when a worn item, a skill, a buff or its age says so. Every test host also checks each
/// reuse against a fresh build (Testing:VerifyStatsCache), so these cover when the memo is used, not its values.
/// </summary>
public sealed class StatsMemoTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestServer.CreateFakeClock();
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartSyntheticAsync(clock: _clock);

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private CharacterService Character => _server.Service<CharacterService>();
    private CharacterStatsService StatsService => _server.Service<CharacterStatsService>();

    /// <summary>One combat work item: opts in and reads the sheet, as a cast or a hit does. Returns the builds it made.</summary>
    private long HotRead(GameSession s, Action? before = null)
    {
        before?.Invoke();
        long builds = StatsService.Builds;
        Character.ReuseStatsInThisWork(s);
        Character.Stats(s);
        return StatsService.Builds - builds;
    }

    private async Task<GameSession> PlayerAsync()
    {
        var bot = await _server.InWorldAsync("Memo");
        var s = _server.Session(bot);
        await _server.OnLoopAsync(() => Assert.Equal(1, HotRead(s))); // the first hot read builds
        return s;
    }

    [Fact]
    public async Task AHotPathReusesTheSheetInLaterWorkItems()
    {
        var s = await PlayerAsync();
        await _server.OnLoopAsync(() => Assert.Equal(0, HotRead(s)));
        await _server.OnLoopAsync(() => Assert.Equal(0, HotRead(s, () => s.CurrentHp = 1))); // HP is applied, not built
        await _server.OnLoopAsync(() =>
        {
            long builds = StatsService.Builds;
            Character.Stats(s); // a path that did not opt in builds fresh
            Assert.Equal(1, StatsService.Builds - builds);
        });
    }

    [Fact]
    public async Task AWornItemChangeOrASkillLevelEndsTheMemoAndBagLootDoesNot()
    {
        var s = await PlayerAsync();
        var loot = new InvItem { Guid = 9_100_001, Entry = 1000000, Stack = 1, Pack = 1 };   // a bag consumable
        var worn = new InvItem { Guid = 9_100_002, Entry = 2010121, Stack = 1, Pack = 2, Grid = 1 }; // worn gear
        await _server.OnLoopAsync(() => Assert.Equal(0, HotRead(s, () => s.Items.Add(loot))));
        await _server.OnLoopAsync(() => Assert.Equal(0, HotRead(s, () => loot.Stack++)));
        await _server.OnLoopAsync(() => Assert.Equal(1, HotRead(s, () => s.Items.Add(worn))));
        // A commit replaces the item's attribute map (CharacterService.CopyItem); the list and its count stay.
        await _server.OnLoopAsync(() => Assert.Equal(1, HotRead(s, () => worn.Attrs = new(worn.Attrs ?? []))));
        await _server.OnLoopAsync(() => Assert.Equal(1, HotRead(s, () => worn.Grid = 0))); // taken off
        await _server.OnLoopAsync(() => s.Items.Remove(worn));
        // Skill levels are copied into the same dictionary (SkillService.CopySkills).
        await _server.OnLoopAsync(() => Assert.Equal(1, HotRead(s, () => s.Progress!.Skills[999_999] = 1)));
        await _server.OnLoopAsync(() => Assert.Equal(0, HotRead(s)));
    }

    [Fact]
    public async Task ABuffReachingItsExpiryEndsTheMemoBeforeItIsRemoved()
    {
        var s = await PlayerAsync();
        await _server.OnLoopAsync(() =>
        {
            var buff = new ActiveBuff(1, 0, s.NowMs + 200, 200, [], 0);
            Assert.Equal(1, HotRead(s, () => s.Buffs.Add(buff))); // a new buff: a new list
            Assert.Equal(0, HotRead(s));
            // The same work item, so BuffService cannot remove it meanwhile: only its expiry ends the memo.
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.Equal(1, HotRead(s));
            Assert.Contains(buff, s.Buffs);
            s.Buffs.Remove(buff);
        });
    }

    [Fact]
    public async Task TheMemoLastsThreeSeconds()
    {
        var s = await PlayerAsync();
        await _server.OnLoopAsync(() =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(3000));
            Assert.Equal(0, HotRead(s));
            _clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, HotRead(s));
        });
    }

    [Fact]
    public async Task ARefreshBuildsAndKeepsItsSheetAndAProjectionNeverServesTheMemo()
    {
        var s = await PlayerAsync();
        await _server.OnLoopAsync(() =>
        {
            long builds = StatsService.Builds;
            Character.Refresh(s); // not a combat path, but a sheet built from the inputs now: the next hot read takes it
            Assert.Equal(1, StatsService.Builds - builds);
            Assert.Equal(0, HotRead(s));
            s.StatsMemoBypass++;
            try { Assert.Equal(1, HotRead(s)); }
            finally { s.StatsMemoBypass--; }
            Assert.Equal(0, HotRead(s)); // the projection neither read nor replaced the role's own sheet
        });
    }
}
