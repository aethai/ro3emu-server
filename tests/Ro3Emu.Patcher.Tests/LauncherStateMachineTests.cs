using Ro3Emu.Patcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class LauncherStateMachineTests
{
    [Theory]
    [InlineData(LauncherStateMachine.Ready, false, LauncherPrimaryAction.Play)]
    [InlineData(LauncherStateMachine.Install, false, LauncherPrimaryAction.Install)]
    [InlineData(LauncherStateMachine.Update, false, LauncherPrimaryAction.Update)]
    [InlineData(LauncherStateMachine.Repair, false, LauncherPrimaryAction.Repair)]
    [InlineData(LauncherStateMachine.Offline, false, LauncherPrimaryAction.RetryCheck)]
    [InlineData(LauncherStateMachine.Error, false, LauncherPrimaryAction.RetryCheck)]
    [InlineData(LauncherStateMachine.Checking, false, LauncherPrimaryAction.None)]
    [InlineData(LauncherStateMachine.Updating, false, LauncherPrimaryAction.None)]
    [InlineData(LauncherStateMachine.Running, false, LauncherPrimaryAction.None)]
    [InlineData(LauncherStateMachine.Ready, true, LauncherPrimaryAction.None)]
    [InlineData(LauncherStateMachine.Install, true, LauncherPrimaryAction.None)]
    public void PrimaryActionRespectsPhaseAndBusyGuard(string phase, bool busy, LauncherPrimaryAction expected) =>
        Assert.Equal(expected, LauncherStateMachine.ResolvePrimary(phase, busy));

    [Theory]
    [InlineData(LauncherStateMachine.Ready, false, true)]
    [InlineData(LauncherStateMachine.Error, false, true)]
    [InlineData(LauncherStateMachine.Running, false, false)]
    [InlineData(LauncherStateMachine.Ready, true, false)]
    public void RepairIsDisallowedWhileBusyOrGameRunning(string phase, bool busy, bool expected) =>
        Assert.Equal(expected, LauncherStateMachine.CanRepair(phase, busy));

    [Theory]
    [InlineData(false, false, 0, LauncherStateMachine.Install)]
    [InlineData(false, true, 5, LauncherStateMachine.Install)]
    [InlineData(true, true, 0, LauncherStateMachine.Update)]
    [InlineData(true, true, 3, LauncherStateMachine.Update)]
    [InlineData(true, false, 2, LauncherStateMachine.Repair)]
    [InlineData(true, false, 0, LauncherStateMachine.Ready)]
    public void PhaseAfterCheckSelectsExpectedTransition(bool installed, bool updateRequired, int changedFiles, string expected) =>
        Assert.Equal(expected, LauncherStateMachine.PhaseAfterCheck(installed, updateRequired, changedFiles));

    [Fact]
    public void FailureCancelAndLaunchTransitionsMatchExpectedRecoveryStates()
    {
        Assert.Equal(LauncherStateMachine.Ready, LauncherStateMachine.PhaseAfterCheckFailure(hasInstalled: true));
        Assert.Equal(LauncherStateMachine.Offline, LauncherStateMachine.PhaseAfterCheckFailure(hasInstalled: false));
        Assert.Equal(LauncherStateMachine.Update, LauncherStateMachine.PhaseAfterCancel(hasInstalled: true));
        Assert.Equal(LauncherStateMachine.Install, LauncherStateMachine.PhaseAfterCancel(hasInstalled: false));
        Assert.Equal(LauncherStateMachine.Running, LauncherStateMachine.PhaseAfterLaunch(started: true));
        Assert.Equal(LauncherStateMachine.Repair, LauncherStateMachine.PhaseAfterLaunch(started: false));
    }

    [Fact]
    public void TransferSpeedMeterComputesRollingWindowRateAndEta()
    {
        var clock = new StepTimeProvider();
        var meter = new TransferSpeedMeter(clock);

        Assert.Equal((0d, 0d), meter.Sample(0, 10_000));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal((0d, 0d), meter.Sample(1_000, 10_000)); // below 1 s minimum window

        clock.Advance(TimeSpan.FromSeconds(1)); // t = 1.5 s, 3000 bytes in 1.5 s => 2000 B/s
        var (rate, eta) = meter.Sample(3_000, 10_000);
        Assert.InRange(rate, 1999, 2001);
        Assert.InRange(eta, 3.49, 3.51);

        // Advance past the 5 s window: old t=0 sample drops out.
        clock.Advance(TimeSpan.FromSeconds(6)); // t = 7.5 s, from t=1.5 s (3000 -> 9000 = 6000 B in 6 s => 1000 B/s)
        var (rate2, eta2) = meter.Sample(9_000, 10_000);
        Assert.InRange(rate2, 999, 1001);
        Assert.InRange(eta2, 0.99, 1.01);
    }

    [Fact]
    public void ThrottledUpdateProgressThrottlesWithinStageAndFlushesImmediatelyOnStageChange()
    {
        var clock = new StepTimeProvider();
        var seen = new List<UpdateProgress>();
        var reporter = new ThrottledUpdateProgress(seen.Add, clock, minIntervalMs: 150);

        reporter.Report(new UpdateProgress(UpdateStage.Downloading, 10, 100, "a.bin"));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        reporter.Report(new UpdateProgress(UpdateStage.Downloading, 20, 100, "a.bin")); // suppressed
        clock.Advance(TimeSpan.FromMilliseconds(120)); // total 170 ms >= 150 ms
        reporter.Report(new UpdateProgress(UpdateStage.Downloading, 30, 100, "a.bin")); // forwarded
        clock.Advance(TimeSpan.FromMilliseconds(10));
        reporter.Report(new UpdateProgress(UpdateStage.Assembling, 30, 100, "a.bin")); // stage changed -> forwarded immediately

        Assert.Equal(3, seen.Count);
        Assert.Equal(10, seen[0].BytesDone);
        Assert.Equal(30, seen[1].BytesDone);
        Assert.Equal(UpdateStage.Assembling, seen[2].Stage);
    }

    private sealed class StepTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }
}
