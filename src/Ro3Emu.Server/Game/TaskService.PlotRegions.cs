using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Server-side safety fallback for plot targets (309002) whose PlotInterpretation row names a scene-editor trigger
/// region (<c>_kTrigger = [scene, region, task]</c>, such as plot 130061 on Prontera 10100 region 101050501 for main
/// story task 10105050 "Strange Occurrences"). Normally the client plays the cutscene on entering that region and
/// reports 84011 (<see cref="OnPlotEndAsync"/>) when it ends, but an unpatched client (missing the
/// LC_TriggerAreaManager_AreaData._equal fix) or a stalled cutscene never sends 84011. Staying in the plot's own
/// trigger region past <see cref="PlotRegionFallbackDelay"/> completes the target so the story cannot softlock, while
/// the dwell window lets normal cutscenes play and finish via 84011 first.
/// </summary>
public sealed partial class TaskService
{
    private Dictionary<long, (long Scene, long Region)>? _plotRegions;
    private TimeSpan _nextPlotRegionScan;
    private readonly ConditionalWeakTable<GameSession, Dictionary<(long TaskId, long PlotId), DateTimeOffset>> _plotRegionEntered = [];

    /// <summary>How long a player must stand in a plot's trigger region before the fallback completes it.</summary>
    internal TimeSpan PlotRegionFallbackDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>plot id -> the scene and region that trigger it; plots without a trigger are absent.</summary>
    private Dictionary<long, (long Scene, long Region)> PlotRegions()
    {
        if (_plotRegions != null) return _plotRegions;
        var regions = new Dictionary<long, (long Scene, long Region)>();
        foreach (var row in PlotInterpretation.All(_data))
        {
            if (row.Id != 0 && row.Trigger is [var scene, var region, ..] && scene != 0 && region != 0)
                regions[row.Id] = (scene, region);
        }
        _plotRegions = regions;
        return regions;
    }

    /// <summary>
    /// Scans online players for running plot targets whose trigger region holds the player for at least
    /// <see cref="PlotRegionFallbackDelay"/> without an 84011 report. Runs every two seconds from
    /// <see cref="PlotRegionTicker"/>.
    /// </summary>
    internal Task ScanPlotRegionsAsync()
    {
        var regions = PlotRegions();
        if (regions.Count == 0) return Task.CompletedTask;
        var now = _clock.GetUtcNow();
        foreach (var session in _sessions.All())
        {
            if (session.Role == null || session.Tasks == null || session.SceneLoading || session.InCutScene)
            {
                if (_plotRegionEntered.TryGetValue(session, out var idle)) idle.Clear();
                continue;
            }
            HashSet<(long TaskId, long PlotId)>? present = null;
            foreach (var info in session.Tasks.Infos.Values.Where(i => i.Status == Running))
            {
                if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
                for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
                {
                    if (info.CompleteNum[index] > 0 || def.Targets[index] is not [TPlot, var plot]
                        || !regions.TryGetValue(plot, out var trigger)) continue;
                    if (session.SceneEntry != trigger.Scene || !_scene.NearKnownRegion(session, trigger.Region)) continue;
                    var key = (info.TaskId, plot);
                    present ??= [];
                    present.Add(key);
                    var entered = _plotRegionEntered.GetOrCreateValue(session);
                    if (!entered.TryGetValue(key, out var since))
                    {
                        entered[key] = now;
                        if (PlotRegionFallbackDelay > TimeSpan.Zero) continue;
                    }
                    else if (now - since < PlotRegionFallbackDelay) continue;
                    entered.Remove(key);
                    present.Remove(key);
                    _log.LogInformation("{Name} waited in trigger region {Region} of plot {Plot} (task {Task}): objective complete",
                        session.Role.Name, trigger.Region, plot, info.TaskId);
                    var owner = session;
                    long matchedPlot = plot;
                    _loop.PostOwned(owner, () => ProgressAsync(owner, TPlot, t => t is [TPlot, var p] && p == matchedPlot ? long.MaxValue : 0),
                        "quest plot region");
                }
            }
            if (_plotRegionEntered.TryGetValue(session, out var tracked) && tracked.Count > 0)
            {
                if (present == null) tracked.Clear();
                else foreach (var stale in tracked.Keys.Where(k => !present.Contains(k)).ToArray()) tracked.Remove(stale);
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
