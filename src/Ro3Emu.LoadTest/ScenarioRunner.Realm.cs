using System.Diagnostics;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.LoadTest;

/// <summary>
/// S7: S6's field fighters plus the background requests a real client sends while it plays (traffic logs of the
/// Genesis client): a flying rider's 31084 FLY_MOVE about once a second (every third bot), and the 82976 activity
/// poll once a minute, which keeps the dispatcher's default Realm admission. It is the mix that queued realm work
/// behind role work on a busy live realm (31084 was Realm too until it was admitted under the flyer's role).
/// </summary>
public sealed partial class ScenarioRunner
{
    private async Task RunRealmChatterAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var clock = Stopwatch.StartNew();
        var tasks = actors.Select(async (actor, index) =>
        {
            try
            {
                bool flyer = index % 3 == 0;
                double nextPoll = index * 60.0 / Math.Max(1, actors.Length);
                for (double t = index * 1.0 / Math.Max(1, actors.Length); !stop.IsCancellationRequested; t += 1)
                {
                    var due = TimeSpan.FromSeconds(t);
                    if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed, stop.Token);
                    if (flyer)
                    {
                        var fly = _counts.Action("realm-fly-move"); fly.Plan(); fly.Offer();
                        await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq
                        { Pos = new Vec3 { X = actor.X, Y = actor.Y + 10, Z = actor.Z }, Dir = new Vec3(), BStop = true }), stop.Token);
                        fly.Complete(0);
                    }
                    if (t < nextPoll) continue;
                    nextPoll += 60;
                    var poll = _counts.Action("realm-activity-poll"); poll.Plan(); poll.Offer();
                    using var attempt = new ActionAttempt(poll);
                    using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(NewActivityService.Common));
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(NewActivityService.Common, new CommonActivityReq
                    { ActivityId = 117, SeasonEveGetInfo = new SeasonEveGetGlobalInfoReq() }), stop.Token);
                    try { await reply.WaitAsync(TimeSpan.FromSeconds(60), stop.Token); attempt.Complete(); }
                    catch (TimeoutException e) { attempt.Fail(e); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        await Task.WhenAll(tasks);
    }
}
