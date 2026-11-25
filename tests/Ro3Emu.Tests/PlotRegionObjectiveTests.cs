using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Story task 10105050 "Strange Occurrences" asks for plot 130061, whose trigger region is Prontera 10100 region
/// 101050501. If an unpatched client or stalled cutscene never sends 84011, standing in the plot's own trigger region
/// past <see cref="TaskService.PlotRegionFallbackDelay"/> completes the objective without interrupting normal
/// cutscenes that report 84011 within the dwell window.
/// </summary>
public sealed class PlotRegionObjectiveTests : AuctionTestsHost
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    [ClientDataFact]
    public async Task StandingInThePlotTriggerRegionCompletesAfterFallbackDelay()
    {
        await using var bot = await InWorld("Alice");
        await Gm(bot, "!quest accept 10105050");
        var region = Service<WorldData>().Map(10100)!.Regions[101050501];
        var tasks = Service<TaskService>();

        // Entering the region does not immediately cut off a playing cutscene.
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var s = Session(bot);
            s.SceneEntry = 10100;
            s.SceneLoading = false;
            s.Position = new Vec3 { X = region.X, Y = region.Y, Z = region.Z };
            await tasks.ScanPlotRegionsAsync();
            return 0;
        });
        Assert.Equal(0, await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Session(bot).Tasks!.CompleteTaskNum.GetValueOrDefault(10105050))));

        // Once the fallback dwell delay elapses without 84011, the plot objective completes and 10105060 opens.
        tasks.PlotRegionFallbackDelay = TimeSpan.Zero;
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            await tasks.ScanPlotRegionsAsync();
            return 0;
        });
        long done = 0;
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(50))
        {
            done = await Service<GameLoop>().InvokeAsync(() =>
                Task.FromResult(Session(bot).Tasks!.CompleteTaskNum.GetValueOrDefault(10105050)));
            if (done > 0) break;
            Assert.True(DateTime.UtcNow < until, "the plot's trigger region did not complete the objective");
        }
        Assert.Equal(1, done);
        Assert.True(await Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Session(bot).Tasks!.Infos.ContainsKey(10105060))));
    }
}
