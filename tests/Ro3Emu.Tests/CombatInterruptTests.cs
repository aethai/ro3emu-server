using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class CombatInterruptTests
{
    [Fact]
    public async Task InterruptWaitsForInFlightCastGateBeforeCancelling()
    {
        await using var server = await TestServer.StartSyntheticAsync();
        await using var bot = await server.InWorldAsync("interrupt1");
        var combat = server.Service<CombatService>();
        var session = server.Session(bot);

        // Simulate an in-flight CastAsync holding SkillCastGate while InterruptAsync (31124) arrives.
        await session.SkillCastGate.WaitAsync();
        Task interruptTask;
        try
        {
            interruptTask = server.OnLoopAsync(() => combat.InterruptAsync(session, new SkillInterruptReq { InterruptSkillId = 0 }));
            Assert.False(interruptTask.IsCompleted);
        }
        finally
        {
            session.SkillCastGate.Release();
        }

        await interruptTask.WaitAsync(TimeSpan.FromSeconds(5));
        var rsp = await TestServer.NextAsync(bot, CombatService.SkillInterrupt, SkillInterruptResp.Parser);
        Assert.False(rsp.IsInterruptSucc);
        Assert.Null(session.SkillCast);
        Assert.Null(session.Attack);
    }
}
