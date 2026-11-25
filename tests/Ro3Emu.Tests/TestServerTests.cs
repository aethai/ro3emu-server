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
}
