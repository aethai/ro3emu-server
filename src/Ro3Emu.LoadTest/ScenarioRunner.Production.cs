using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.LoadTest;

/// <summary>
/// S10: a replay of a small production realm's capture (#484: 4.1 h, 11 players). In every group of 11 bots, 6 farm on
/// auto battle with Bonus Time running, on the field maps the capture listed (two of them share the first one) and
/// Geffen Tower 3; the other 5 stand around in Prontera and take a step now and then. The capture's per-farmer rates
/// were 1.45 casts/s (31102) and 3.7 own-auction reads/s (82005). The hard run is --bots 132 --cast-interval-ms 250
/// --disk-delay-ms 15 (about 30 hits/s on a VPS-like disk); run it for hours with --snapshot-minutes to see whether the
/// loop degrades over time, and with RO3EMU_DB_SYNCHRONOUS=NORMAL as that server.
/// </summary>
public sealed partial class ScenarioRunner
{
    private const int ProductionCastIntervalMs = 690, ProductionAuctionReadIntervalMs = 270, TownStepIntervalMs = 4000;
    private static readonly long[] ProductionFarmMaps = [10312, 10312, 10240, 10110, 10143, 10352];
    private const long ProductionTown = 10100;
    private const float ProductionReach = 3f; // the client's auto battle walks into melee range; 8 m drew SkillDistance
    private const uint SetAutoBattle = 31054; // ActivitiesMessages: 31054 SetAutoBattleReq, no reply

    private static bool ProductionFarmer(int index) => index % 11 < 6;

    private async Task PrepareProductionAsync(CancellationToken ct)
    {
        var kits = AutoBattleKits();
        var fieldMaps = AutoBattleMaps();
        using var gate = new SemaphoreSlim(12);
        await Task.WhenAll(_actors.Select(async (actor, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!ProductionFarmer(i))
                {
                    await TravelAsync(actor, ProductionTown, "setup-town", ct);
                    return;
                }
                var kit = kits[i % kits.Length];
                actor.Bar = kit.Bar;
                actor.RestUntil = new();
                actor.Attacks = kit.Attacks;
                using (var applied = actor.Bot.Inbox.Expect(e => e.IsGame(GmCommands.GeneralProcGm)))
                {
                    await actor.Bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmListId = kit.Preset });
                    var answer = GeneralGMProcRsp.Parser.ParseFrom((await applied.WaitAsync(TimeSpan.FromSeconds(60), ct)).Body);
                    if (answer.ErrInfo.Contains("not enabled", StringComparison.Ordinal))
                        throw new InvalidOperationException($"GM preset {kit.Preset} refused: {answer.ErrInfo}");
                }
                // The first 11 bots farm the capture's maps; further groups spread over every open field map with spawns,
                // a few farmers on each, so a scaled run is not capped by one map's monster supply.
                long map = i < 11 ? ProductionFarmMaps[i % 11 % ProductionFarmMaps.Length] : fieldMaps[(i / 11 * 6 + i % 11) % fieldMaps.Length];
                try { await TravelAsync(actor, map, "setup-farm-map", ct); }
                catch (InvalidOperationException) when (i >= 11) { } // a map the role cannot reach: it farms where it stands
                await actor.Bot.SendGameAsync(SetAutoBattle, new SetAutoBattleReq { IsAutoBattle = true });
                using var started = actor.Bot.Inbox.Expect(e => e.IsGame(WildAfkService.Start));
                await actor.Bot.SendGameAsync(WildAfkService.Start, new StartHighMultiplierTimeReq());
                var reply = StartHighMultiplierTimeRsp.Parser.ParseFrom((await started.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
                Count("setup-bonus-time-" + reply.Result);
                if (reply.Result is not (0 or ErrorCodes.AfkAlreadyActive)) throw new InvalidOperationException($"Bonus Time refused: {reply.Result}");
            }
            catch (Exception e) { _failures[300000 + i] = $"production setup: {e.GetType().Name}: {e.Message}"; }
            finally { gate.Release(); }
        }));
        if (!_failures.IsEmpty) throw new InvalidOperationException($"Production setup failed for {_failures.Count} bots: {_failures.First().Value}");
    }

    private async Task RunProductionAsync(int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var bots = _actors.Select((actor, i) => ProductionFarmer(i)
            ? FarmAsync(actor, i, stop.Token, options.CastIntervalMs > 0 ? options.CastIntervalMs : ProductionCastIntervalMs, ProductionAuctionReadIntervalMs, ProductionReach)
            : TownIdleAsync(actor, i, stop.Token));
        await Task.WhenAll(bots.Append(SnapshotLoopAsync(stop.Token)));
    }

    /// <summary>A player standing in town: a short step every few seconds and a ping every 6.4 s.</summary>
    private async Task TownIdleAsync(BotActor actor, int index, CancellationToken ct)
    {
        var random = new Random(index);
        float homeX = actor.X, homeZ = actor.Z;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(index * 97 % TownStepIntervalMs), ct);
            for (int n = 0; !ct.IsCancellationRequested; n++)
            {
                float x = homeX + (float)(random.NextDouble() * 6 - 3), z = homeZ + (float)(random.NextDouble() * 6 - 3);
                Count("town-walk");
                await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, stop: true), ct);
                actor.X = x; actor.Z = z;
                if (n % 2 == 1) await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), ct);
                await Task.Delay(TownStepIntervalMs, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { _failures[500000 + index] = $"town: {e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>--snapshot-minutes: the server's cumulative metrics every N minutes, as {prefix}-tNNNm.json.</summary>
    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        if (options.SnapshotMinutes <= 0) return;
        try
        {
            for (int minutes = options.SnapshotMinutes; ; minutes += options.SnapshotMinutes)
            {
                await Task.Delay(TimeSpan.FromMinutes(options.SnapshotMinutes), ct);
                var text = await _http.GetStringAsync("/ro3emu/metrics", ct);
                await File.WriteAllTextAsync($"{options.OutputPrefix}-t{minutes:000}m.json", text, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
