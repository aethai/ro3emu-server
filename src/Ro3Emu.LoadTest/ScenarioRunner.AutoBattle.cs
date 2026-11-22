using System.Diagnostics;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.LoadTest;

/// <summary>
/// S9: level 99 players on auto battle, the traffic of a live realm's farming players (#484's 2.5 h production capture
/// of six such players, per player and second: 4.9 casts 31102, 1.9 own-auction reads 82005, 0.29 walks, 0.16 pings,
/// 0.13 target locks). Each bot takes one of the client's level 99 GM presets (GMCombinationConfig: job, skill build,
/// skill bar, weapon, emblems, mount and season talents), then farms on one of the open field maps with spawns, a few
/// players per map as on that server. Casts follow the bar in order on the nearest monster without waiting for replies,
/// as the client's auto battle does; the server refuses what is on cooldown and runs its own auto attack in between.
/// </summary>
public sealed partial class ScenarioRunner
{
    private const int CastIntervalMs = 205, AuctionReadIntervalMs = 525, PingIntervalMs = 6400;
    private const float CastReach = 8f, StepMetres = 2.5f;

    private sealed record AutoBattleKit(int Preset, int[] Bar);

    /// <summary>GMCombinationConfig rows that set level 99 and a skill bar (GmsetSkillWheel), in id order.</summary>
    private AutoBattleKit[] AutoBattleKits()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.RepositoryRoot, "data", "config", "GMCombinationConfig.json")));
        var kits = new List<AutoBattleKit>();
        foreach (var row in doc.RootElement.EnumerateObject().OrderBy(p => int.Parse(p.Name)))
        {
            if (!row.Value.TryGetProperty("_kCombinationScript", out var script)) continue;
            var steps = script.EnumerateArray().Select(s => s.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()).ToArray();
            if (!steps.Any(s => s is ["GmSetLevel", "99", ..])) continue;
            var wheel = steps.FirstOrDefault(s => s.Length > 1 && s[0] == "GmsetSkillWheel");
            int[] bar = wheel?.Skip(1).Select(int.Parse).Where(id => id > 0).ToArray() ?? [];
            if (bar.Length > 0) kits.Add(new(int.Parse(row.Name), bar));
        }
        return kits.Count > 0 ? [.. kits] : throw new InvalidOperationException("No level 99 GM preset with a skill bar");
    }

    /// <summary>Open field maps (MapConfig _iSceneType 1) with monster spawns, each kept to a handful of farmers.</summary>
    private long[] AutoBattleMaps()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.RepositoryRoot, "data", "config", "MapConfig.json")));
        return [.. doc.RootElement.EnumerateObject()
            .Where(p => p.Value.TryGetProperty("_iSceneType", out var t) && t.GetInt32() == 1 && long.TryParse(p.Name, out _))
            .Select(p => long.Parse(p.Name))
            .Where(id => _world.Map(id) is { SpawnGroups.Count: > 0 })
            .Order()];
    }

    private async Task PrepareAutoBattleAsync(CancellationToken ct)
    {
        var kits = AutoBattleKits();
        var maps = AutoBattleMaps();
        using var gate = new SemaphoreSlim(12);
        await Task.WhenAll(_actors.Select(async (actor, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var kit = kits[i % kits.Length];
                actor.Bar = kit.Bar;
                using var applied = actor.Bot.Inbox.Expect(e => e.IsGame(GmCommands.GeneralProcGm));
                await actor.Bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmListId = kit.Preset });
                var answer = GeneralGMProcRsp.Parser.ParseFrom((await applied.WaitAsync(TimeSpan.FromSeconds(60), ct)).Body);
                if (answer.ErrInfo.Contains("not enabled", StringComparison.Ordinal))
                    throw new InvalidOperationException($"GM preset {kit.Preset} refused: {answer.ErrInfo}");
                long map = maps[i % maps.Length];
                try { await TravelAsync(actor, map, "setup-farm-map", ct); }
                catch (InvalidOperationException) { } // a map the role cannot reach: it farms where it stands
            }
            catch (Exception e) { _failures[300000 + i] = $"auto-battle setup: {e.GetType().Name}: {e.Message}"; }
            finally { gate.Release(); }
        }));
        if (!_failures.IsEmpty) throw new InvalidOperationException($"Auto-battle setup failed for {_failures.Count} bots");
    }

    private async Task RunAutoBattleAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        await Task.WhenAll(actors.Select((actor, index) => FarmAsync(actor, index, stop.Token)));
    }

    /// <summary>One farming player: a cast every 205 ms (walking up first when the target is out of reach), its own
    /// auction list every 525 ms and a ping every 6.4 s, each on its own clock with a per-bot phase.</summary>
    private async Task FarmAsync(BotActor actor, int index, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        double phase = (index * 37 % 1000) / 1000.0;
        double nextCast = phase * CastIntervalMs, nextAuction = phase * AuctionReadIntervalMs, nextPing = phase * PingIntervalMs;
        int barSlot = 0;
        ulong locked = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double due = Math.Min(nextCast, Math.Min(nextAuction, nextPing));
                double wait = due - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                double now = clock.Elapsed.TotalMilliseconds;
                if (now >= nextPing)
                {
                    nextPing += PingIntervalMs;
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), ct);
                }
                if (now >= nextAuction)
                {
                    nextAuction += AuctionReadIntervalMs;
                    Count("farm-auction-read");
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(AuctionService.Self, new QuerySelfAuctionReq()), ct);
                }
                if (now < nextCast) continue;
                nextCast += CastIntervalMs;
                var target = actor.Reducer.Monsters().OrderBy(m => Distance(actor, m)).FirstOrDefault();
                if (target == null) { Count("farm-search"); continue; }
                float distance = Distance(actor, target);
                if (distance > CastReach)
                {
                    await StepTowardAsync(actor, target, distance, ct);
                    continue;
                }
                if (target.Uuid != locked)
                {
                    locked = target.Uuid;
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(CombatService.LockEnemy,
                        new LockEnemyReq { EnemyId = (long)target.Uuid }), ct);
                }
                int skill = actor.Bar[barSlot++ % actor.Bar.Length];
                Count("farm-cast");
                var reply = actor.Bot.Inbox.Expect(e => e.IsGame(CombatService.UseSkill));
                await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(CombatService.UseSkill,
                    new CastSkillReq { SkillId = skill, TargetId = (long)target.Uuid, TargetPos = target.MovementInfo.Pos }), ct);
                _ = TallyCastAsync(reply, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { _failures[400000 + index] = $"farm: {e.GetType().Name}: {e.Message}"; }
    }

    private async Task StepTowardAsync(BotActor actor, EntityBase target, float distance, CancellationToken ct)
    {
        var pos = target.MovementInfo.Pos;
        float step = MathF.Min(StepMetres, distance - CastReach + 1f);
        float x = actor.X + (pos.X - actor.X) / distance * step, z = actor.Z + (pos.Z - actor.Z) / distance * step;
        Count("farm-walk");
        await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, stop: true), ct);
        actor.X = x; actor.Z = z;
    }

    /// <summary>The cast's 31102 answer by result code (farm-cast-0 = accepted), without holding up the next cast.</summary>
    private async Task TallyCastAsync(BotExpectation reply, CancellationToken ct)
    {
        using (reply)
        {
            try
            {
                var answer = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body);
                Count("farm-cast-" + answer.Result);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Count("farm-cast-unanswered"); }
        }
    }

    private void Count(string action)
    {
        var tally = _counts.Action(action);
        tally.Plan(); tally.Offer(); tally.Complete(0);
    }
}
