using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Buffs that outlive a logout (inferred): BuffConfig._iOffline 2 is not read by the client, and its meaning comes from
/// the rows that carry it, such as all two-hour food. A timed row that a scene does not clear returns at login with the
/// end it had, so the time keeps running while the player is away. Zero and -1 time rows belong to the system that
/// granted them, and _iOffline 1 stays unapplied.
/// </summary>
public sealed partial class BuffService
{
    private sealed record OfflineChange(long Attr, double Value);

    private sealed record OfflineBuff(int ConfigId, long ExpireMs, int DurationMs, long SourceId, int PileCount, OfflineChange[] Changes);

    /// <summary>role.offline_buffs: an object, as every role JSON blob the economy snapshot reads.</summary>
    private sealed record OfflineBuffState(OfflineBuff[] Buffs);

    private BuffConfig? SurvivesLogout(int configId) =>
        _data.Row("BuffConfig", configId) is { } row && new BuffConfig(row) is { Offline: 2, SceneClear: 0, Time: > 0 } config ? config : null;

    /// <summary>What the role's logout commit keeps. A buff with a live shield is not kept: its amount came from its caster.</summary>
    internal byte[] OfflineState(GameSession p)
    {
        long now = MonsterService.NowMs;
        OfflineBuff[] kept = [.. p.Buffs
            .Where(b => !b.IsPassive && !b.IsTotemOwned && !b.IsGuardianOwned && b.ShieldRemaining == 0 && b.ExpireMs > now && SurvivesLogout(b.ConfigId) != null)
            .Select(b => new OfflineBuff(b.ConfigId, b.ExpireMs, b.DurationMs, b.SourceId, b.PileCount, [.. b.Changes.Select(c => new OfflineChange(c.Attr, c.Value))]))];
        return JsonSerializer.SerializeToUtf8Bytes(new OfflineBuffState(kept));
    }

    /// <summary>Puts the kept buffs back on a role being selected; entering the scene sends them (ResendAsync).</summary>
    internal void RestoreOffline(GameSession p, byte[]? state)
    {
        if (state == null) return;
        OfflineBuff[]? saved;
        try { saved = JsonSerializer.Deserialize<OfflineBuffState>(state)?.Buffs; }
        catch (JsonException) { return; }
        long now = MonsterService.NowMs;
        foreach (var kept in saved ?? [])
        {
            if (kept.ExpireMs <= now || SurvivesLogout(kept.ConfigId) is not { } config) continue;
            var buff = new ActiveBuff(Interlocked.Increment(ref _guids), kept.ConfigId, kept.ExpireMs, kept.DurationMs,
                [.. kept.Changes.Select(c => (c.Attr, c.Value))], kept.SourceId, kept.PileCount,
                StateOrdinal: StateFor(kept.ConfigId, (int)config.BuffId, false));
            p.Buffs.Add(buff);
            TrackPeriodic(p, null, buff);
            TrackHalo(p, buff);
        }
    }
}
