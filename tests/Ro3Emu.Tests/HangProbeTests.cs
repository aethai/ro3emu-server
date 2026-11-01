namespace Ro3Emu.Tests;

public sealed class HangProbeTests
{
    [Fact]
    public void Hangs() => Thread.Sleep(Timeout.Infinite);
}
