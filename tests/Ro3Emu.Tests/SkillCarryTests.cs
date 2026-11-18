using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The walks a client reports around a 31112 that carries its player (SkillCarry, RoleSessionService.WalkAsync). The client
/// applies the move only when it gets it, so at a ping above the move's time a walk it sent before, or the stop its
/// StopMove sends from the move's start, arrives after the move; taken, it put the server back where the move began. On
/// the invented fixture, which has no nav grid: the rule holds whatever skill moves the player.
/// </summary>
public sealed class SkillCarryTests
{
    private static GameSession Session(SyntheticHost host, BotClient bot) =>
        host.Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private static async Task<Vec3> PositionAsync(SyntheticHost host, BotClient bot)
    {
        await bot.HeartbeatAsync(); // every message before it has been handled
        return await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(host, bot).Position.Clone()));
    }

    private static bool Corrected(SyntheticHost host, BotClient bot) => bot.Inbox.Snapshot().Any(e =>
        e.IsGame(GameAoiSink.MovementChange) && MovementChangeNtf.Parser.ParseFrom(e.Body).Uid == Session(host, bot).Role!.Id);

    [Fact]
    public async Task WalksSentBeforeADashThatArriveAfterItDoNotPutThePlayerBack()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.WalkCheck = "enforce");
        await using var bot = await host.InWorldAsync("Dasher");
        var here = await PositionAsync(host, bot);
        await bot.WalkAsync(here.X + 0.5f, here.Y, here.Z); // walking toward the target when the charge starts
        var start = await PositionAsync(host, bot);
        var end = new Vec3 { X = start.X + 6, Y = start.Y, Z = start.Z };
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            Session(host, bot).CarryBySkill(start, end, MonsterService.NowMs, 100); // Heavy Axe Smash's 100 ms charge
            return Task.CompletedTask;
        });
        // A round trip of 250 ms: the step along the old walk and the stop from the move's start come after its 100 ms.
        await Task.Delay(250);
        await bot.WalkAsync(start.X + 0.8f, start.Y, start.Z);
        await bot.WalkAsync(start.X, start.Y, start.Z, stop: true);
        Assert.Equal(end.X, (await PositionAsync(host, bot)).X, 2);

        // The walk from the landing is taken, with no correction.
        await bot.WalkAsync(end.X + 1, end.Y, end.Z, stop: true);
        Assert.Equal(end.X + 1, (await PositionAsync(host, bot)).X, 2);
        Assert.False(Corrected(host, bot));
        Assert.Equal(0, host.Service<CheatMonitor>().LoggedCount(bot.Account!, CheatMonitor.Walk));

        // Once a round trip has passed, walking back to where the charge began is a walk like any other.
        await Task.Delay((int)SkillCarry.LateWalkMs);
        await bot.WalkAsync(start.X, start.Y, start.Z, stop: true);
        Assert.Equal(start.X, (await PositionAsync(host, bot)).X, 2);
        Assert.False(Corrected(host, bot));
    }

    [Fact]
    public async Task TheDebugSendDelayHoldsWhatTheGameGateSends()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.DebugSendDelayMs = 300);
        await using var bot = await host.InWorldAsync("Laggy");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await bot.HeartbeatAsync();
        Assert.InRange(watch.ElapsedMilliseconds, 290, 5000);
    }

    [Fact]
    public async Task AWalkPastTheLandingIsStillHeldToTheSpeed()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.WalkCheck = "enforce");
        await using var bot = await host.InWorldAsync("Blinker");
        var start = await PositionAsync(host, bot);
        var end = new Vec3 { X = start.X + 6, Y = start.Y, Z = start.Z };
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            Session(host, bot).CarryBySkill(start, end, MonsterService.NowMs, 100);
            return Task.CompletedTask;
        });
        await Task.Delay(250);
        // Nearer the landing than the start is no walk from before the move: the walk check measures it from the landing.
        await bot.WalkAsync(end.X + 200, end.Y, end.Z, stop: true);
        Assert.Equal(end.X, (await PositionAsync(host, bot)).X, 2);
        Assert.Equal(1, host.Service<CheatMonitor>().LoggedCount(bot.Account!, CheatMonitor.Walk));
        Assert.True(Corrected(host, bot));
    }
}
