using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Main story 10105050 "Strange Occurences" asks for plot 130061, and its cutscene (cutscene_xinshou_intosxzt) is not
/// in the shipped client files: the client can never play it and never reports 84011, which left the story quest
/// unfinished for good. The plot's own scene-editor trigger region (Prontera 10100, region 101050501) completes the
/// objective instead; the plot-end packet still completes it first whenever a client can play the story.
/// </summary>
public sealed class PlotRegionObjectiveTests : AuctionTestsHost
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    [ClientDataFact]
    public async Task StandingInThePlotTriggerRegionCompletesTheStoryObjective()
    {
        await using var bot = await InWorld("Alice");
        await Gm(bot, "!quest accept 10105050");
        var region = Service<WorldData>().Map(10100)!.Regions[101050501];
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var s = Session(bot);
            s.SceneEntry = 10100;
            s.SceneLoading = false;
            s.Position = new Vec3 { X = region.X, Y = region.Y, Z = region.Z };
            await Service<TaskService>().ScanPlotRegionsAsync();
            return 0;
        });
        long done = 0;
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(50))
        {
            done = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(bot).Tasks!.CompleteTaskNum.GetValueOrDefault(10105050)));
            if (done > 0) break;
            Assert.True(DateTime.UtcNow < until, "the plot's trigger region did not complete the objective");
        }
        Assert.Equal(1, done);
    }
}
