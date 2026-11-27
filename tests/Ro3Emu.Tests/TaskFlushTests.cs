using Microsoft.Extensions.Time.Testing;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// The quest flush after a kill (TaskService.SettleAsync countsOnly): a kill that moved no quest state sends the
/// quests' counts and skips the datasets that read only quest states and the character (functions 5056, talents
/// 5050, Growth Class 5045, daily tracking), which every other settle still refreshes.
/// </summary>
public sealed class TaskFlushTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestServer.CreateFakeClock();
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartSyntheticAsync(clock: _clock);

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    private static long FunctionVersion(GameSession s)
    {
        lock (s.Profile.SyncVersions) return s.Profile.SyncVersions.GetValueOrDefault(FunctionService.FunctionOpenSync);
    }

    [Fact]
    public async Task AKillThatMovesNoQuestSkipsTheQuestStateDatasets()
    {
        var bot = await _server.InWorldAsync("Flush");
        var s = _server.Session(bot);
        var tasks = _server.Service<TaskService>();
        var owners = _server.Service<OwnerRegistry>();
        await _server.OnLoopAsync(() => owners.RunAsync("test/settle", [ReservationKey.Role(s.Role!.Id)], async () =>
        {
            s.Tasks ??= new Romsg.DBTaskInfo(); // the synthetic data set has no quest chain to start
            long before = FunctionVersion(s);
            var memo = new object();
            s.StatsMemo = memo;
            await tasks.SettleAsync(s, countsOnly: true);
            Assert.Equal(before, FunctionVersion(s)); // 5056 not even compared
            Assert.Same(memo, s.StatsMemo);           // the stat memo survives a count

            await tasks.SettleAsync(s);
            Assert.Equal(before + 1, FunctionVersion(s)); // any other settle still refreshes 5056
            Assert.Null(s.StatsMemo);
        }));
    }
}
