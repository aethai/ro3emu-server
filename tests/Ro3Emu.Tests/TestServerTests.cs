using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TestServerTests
{
    [Fact]
    public async Task SyntheticServerStartsRestartsAndDeletesTempDirWithoutClearAllPools()
    {
        string dir;
        await using (var server = await TestServer.StartSyntheticAsync())
        {
            dir = server.DataDir;
            await using (var bot = await server.InWorldAsync("Tester"))
            {
                await server.UntilAsync(() => server.Session(bot).SceneEntry == SyntheticData.Town, "entered town");
                Assert.Equal("tester", bot.Account);
                Assert.NotNull(server.Session(bot).Role);
            }

            await server.RestartAsync();

            await using var back = await server.InWorldAsync("Tester", create: false);
            await server.UntilAsync(() => server.Session(back).SceneEntry == SyntheticData.Town, "re-entered town after restart");
        }

        // AccountStore uses Pooling=False, so the temp directory must be gone without SqliteConnection.ClearAllPools().
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task FakeClockAdvancesGameLoopWithoutSleeping()
    {
        var clock = TestServer.CreateFakeClock();
        await using var server = await TestServer.StartSyntheticAsync(clock: clock);
        var fired = false;
        await server.OnLoopAsync(() => server.Loop.Schedule(TimeSpan.FromSeconds(15), () => { fired = true; return Task.CompletedTask; }));

        var wall = System.Diagnostics.Stopwatch.StartNew();
        await server.AdvanceAsync(TimeSpan.FromSeconds(15));
        wall.Stop();

        Assert.True(fired);
        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(5), $"AdvanceAsync(15s) took {wall.Elapsed.TotalMilliseconds:F0} ms");
    }
}
