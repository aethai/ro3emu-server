using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Plot targets (309002) whose plot names a scene-editor trigger region. PlotInterpretation._kTrigger is
/// [scene, region, task] (130061 "cutscene_xinshou_intosxzt" triggers on Prontera 10100 region 101050501 for task
/// 10105050 "Strange Occurences"). The client plays the plot when the player walks into that region and reports 84011
/// when it ends, but a client whose cutscene asset is missing (that prefab is absent from the shipped client files)
/// can never send the report, and the story quest then could never be finished. Standing in the plot's own region
/// completes the target as well, so the main story cannot stall there.
/// </summary>
public sealed partial class TaskService
{
    private Dictionary<long, (long Scene, long Region)>? _plotRegions;
    private TimeSpan _nextPlotRegionScan;

    /// <summary>plot id -> the scene and region that trigger it; plots without one are absent.</summary>
    private Dictionary<long, (long Scene, long Region)> PlotRegions()
    {
        if (_plotRegions != null) return _plotRegions;
        var regions = new Dictionary<long, (long Scene, long Region)>();
        foreach (var row in _data.Table("PlotInterpretation").Values)
        {
            long id = GameData.Int(row, "_iID");
            if (id == 0 || !row.TryGetProperty("_kTrigger", out var trigger) || trigger.ValueKind != JsonValueKind.Array) continue;
            var values = trigger.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            if (values is [var scene, var region, ..] && scene != 0 && region != 0) regions[id] = (scene, region);
        }
        _plotRegions = regions;
        return regions;
    }

    /// <summary>
    /// Completes a running plot target whose trigger region holds the player. Runs from <see cref="PlotRegionTicker"/>
    /// every two seconds; the plot-end packet still completes it first when the client can play the story.
    /// </summary>
    internal Task ScanPlotRegionsAsync()
    {
        var regions = PlotRegions();
        if (regions.Count == 0) return Task.CompletedTask;
        foreach (var session in _sessions.All())
        {
            if (session.Role == null || session.Tasks == null || session.SceneLoading) continue;
            foreach (var info in session.Tasks.Infos.Values.Where(i => i.Status == Running))
            {
                if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
                for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
                {
                    if (info.CompleteNum[index] > 0 || def.Targets[index] is not [TPlot, var plot]
                        || !regions.TryGetValue(plot, out var trigger)) continue;
                    if (session.SceneEntry != trigger.Scene || !_scene.NearRegion(session, trigger.Region)) continue;
                    _log.LogInformation("{Name} enters the trigger region of plot {Plot} (task {Task}): objective complete",
                        session.Role.Name, plot, info.TaskId);
                    var owner = session;
                    _loop.Post(() => ProgressAsync(owner, TPlot, t => t is [TPlot, var p] && p == plot ? long.MaxValue : 0));
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>The game loop's two-second scan for <see cref="ScanPlotRegionsAsync"/>.</summary>
    private sealed class PlotRegionTicker(TaskService owner) : IGameTicker
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

        public void Tick(GameTime time)
        {
            if (time.Now < owner._nextPlotRegionScan) return;
            owner._nextPlotRegionScan = time.Now + Interval;
            owner._loop.Post(owner.ScanPlotRegionsAsync);
        }
    }
}
