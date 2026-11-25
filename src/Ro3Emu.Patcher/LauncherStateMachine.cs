namespace Ro3Emu.Patcher;

/// <summary>Primary button action resolved from the launcher's current phase and busy state.</summary>
public enum LauncherPrimaryAction
{
    None,
    Play,
    Install,
    Update,
    Repair,
    RetryCheck,
}

/// <summary>
/// Pure phase-transition rules for the player launcher UI so every state transition can be verified
/// cross-platform without WPF or WebView2.
/// </summary>
public static class LauncherStateMachine
{
    public const string Checking = "checking";
    public const string Install = "install";
    public const string Update = "update";
    public const string Repair = "repair";
    public const string Ready = "ready";
    public const string Updating = "updating";
    public const string Launching = "launching";
    public const string Running = "running";
    public const string Offline = "offline";
    public const string Error = "error";

    public static LauncherPrimaryAction ResolvePrimary(string phase, bool workInProgress)
    {
        if (workInProgress) return LauncherPrimaryAction.None;
        return phase switch
        {
            Ready => LauncherPrimaryAction.Play,
            Install => LauncherPrimaryAction.Install,
            Update => LauncherPrimaryAction.Update,
            Repair => LauncherPrimaryAction.Repair,
            Offline or Error => LauncherPrimaryAction.RetryCheck,
            _ => LauncherPrimaryAction.None,
        };
    }

    public static bool CanRepair(string phase, bool workInProgress) =>
        !workInProgress && phase != Running;

    public static string PhaseAfterCheck(bool hasInstalled, bool updateRequired, int changedFileCount)
    {
        if (!hasInstalled) return Install;
        if (updateRequired) return Update;
        if (changedFileCount > 0) return Repair;
        return Ready;
    }

    public static string PhaseAfterCheckFailure(bool hasInstalled) =>
        hasInstalled ? Ready : Offline;

    public static string PhaseAfterCancel(bool hasInstalled) =>
        hasInstalled ? Update : Install;

    public static string PhaseAfterLaunch(bool started) =>
        started ? Running : Repair;
}

/// <summary>Download speed over the last ~5 seconds and the remaining seconds at that speed.</summary>
public sealed class TransferSpeedMeter(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly long _start = (clock ?? TimeProvider.System).GetTimestamp();
    private readonly Queue<(double At, long Bytes)> _samples = new();

    public (double Rate, double Eta) Sample(long done, long total)
    {
        double now = _clock.GetElapsedTime(_start).TotalSeconds;
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().At > 5) _samples.Dequeue();
        var first = _samples.Peek();
        double seconds = now - first.At;
        if (seconds < 1) return (0, 0);
        double rate = (done - first.Bytes) / seconds;
        return rate <= 0 ? (0, 0) : (rate, Math.Max(0, total - done) / rate);
    }
}

/// <summary>
/// Forwards at most one update progress report per <paramref name="minIntervalMs"/> within a stage,
/// and forwards immediately whenever <see cref="UpdateProgress.Stage"/> changes.
/// </summary>
public sealed class ThrottledUpdateProgress(Action<UpdateProgress> show, TimeProvider? clock = null,
    long minIntervalMs = 150) : IProgress<UpdateProgress>
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly long _start = (clock ?? TimeProvider.System).GetTimestamp();
    private long _lastShown = long.MinValue;
    private UpdateStage? _stage;

    public void Report(UpdateProgress value)
    {
        lock (_gate)
        {
            long now = (long)_clock.GetElapsedTime(_start).TotalMilliseconds;
            if (_stage == value.Stage && _lastShown != long.MinValue && now - _lastShown < minIntervalMs) return;
            _stage = value.Stage;
            _lastShown = now;
        }
        show(value);
    }
}
