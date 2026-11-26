using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Hatred: a monster keeps, for each player who engaged it since it last left combat, how much hatred he has earned
/// (<see cref="MonsterUnit.ThreatTable"/>). Damage adds its amount, a heal a share of it on the monsters that hate the
/// healed player, Provoke puts the caster on top. A fighting monster turns from its target only to a player whose hatred
/// exceeds the target's by PublicParamConfig HatredExceedValue; entries decline (AIConfig _iHatredDecline*) and are
/// dropped when stale or far (HateClearTime, HateClearDistance). No client code runs these rules: the client has the
/// parameters and shows the result (31051, 31052, a boss's hatred target), so how they apply is inferred where noted.
/// </summary>
public sealed partial class MonsterService
{
    /// <summary>(inferred) Provoke / Taunt put the caster this much above the highest hatred on the monster, so that he
    /// holds it until another player out-earns him by that much and HatredExceedValue. No client table holds a value.</summary>
    internal const long ProvokeHatredCushion = 50_000;

    private const long BasisPoints = 10_000;

    private HatredRules? _hatredRules;

    /// <summary>PublicParamConfig's hatred parameters, read once.</summary>
    private HatredRules HatredParams => _hatredRules ??= HatredRules.Of(PublicParams.Of(_data));

    private sealed record HatredRules(long ExceedBp, long HealBp, float ClearDistance, long ClearTimeMs)
    {
        public static HatredRules Of(PublicParams p) => new(
            // HatredExceedValue 3000 (inferred): per 10000 of the target's hatred, so another player takes the target
            // with more than 130 % of its hatred.
            Math.Max(0L, p.HatredExceedValue ?? 0),
            // HealthHatredScale 5000 (inferred): per 10000 of the HP healed, so a heal earns half its amount.
            Math.Max(0L, p.HealthHatredScale ?? 0),
            // HateClearDistance 3000 (inferred): centimetres, as AIConfig's distances (_iChaseDistance, _iViewRange,
            // _iUnFightParam) are, so 30 m between the monster and the player.
            Math.Max(0L, p.HateClearDistance ?? 0) / 100f,
            // HateClearTime 10000 (inferred): milliseconds in which the entry did not grow.
            Math.Max(0L, p.HateClearTime ?? 0));
    }

    /// <summary>A player's hatred on a monster grows by <paramref name="amount"/> (his damage on it, or his heal's share):
    /// an idle monster engages him, a fighting one turns to him once his hatred exceeds its target's by HatredExceedValue.</summary>
    public void AddHatred(MonsterUnit m, GameSession source, long amount)
    {
        if (!MayHate(m, source)) return;
        var entry = Hate(m, source);
        entry.Value = Sum(entry.Value, Math.Max(0, amount));
        if (m.Target == null) Engage(m, source);
        else if (!ReferenceEquals(m.Target, source) && Exceeds(m, entry.Value)) TurnTo(m, source);
    }

    /// <summary>Provoke / Taunt: the caster takes the monster's target, his hatred <see cref="ProvokeHatredCushion"/> above
    /// the highest on it (inferred). An idle monster enters the fight through <see cref="Engage(MonsterUnit, GameSession)"/>,
    /// with its battle start, first swing and battle call.</summary>
    public void Provoke(MonsterUnit m, GameSession source)
    {
        if (!MayHate(m, source)) return;
        long top = m.ThreatTable.Count == 0 ? 0 : m.ThreatTable.Values.Max(r => r.Value);
        var entry = Hate(m, source);
        entry.Value = Math.Max(entry.Value, Sum(top, ProvokeHatredCushion));
        if (m.Target == null) Engage(m, source);
        else TurnTo(m, source);
    }

    /// <summary>HealthHatredScale: a heal earns its healer that share of the HP healed as hatred on each monster that
    /// hates the healed player (inferred: the monsters already fighting him, never an idle one).</summary>
    public void AddHealHatred(GameSession healer, GameSession healed, long healedAmount)
    {
        long share = (long)((Int128)Math.Max(0, healedAmount) * HatredParams.HealBp / BasisPoints);
        if (share <= 0 || healed.Role == null || healed.SceneLoading) return;
        // The whole map: a monster's target may stand beyond HateClearDistance while it is within its leash.
        foreach (var unit in _world.UnitsIn(healed.WorldScene).ToArray())
            if (unit is MonsterUnit { ThreatTable.Count: > 0 } m && m.ThreatTable.ContainsKey(healed)) AddHatred(m, healer, share);
    }

    /// <summary>The player's entry, created at 0 when he first engages it, refreshed for HateClearTime.</summary>
    private MonsterUnit.ThreatRecord Hate(MonsterUnit m, GameSession player)
    {
        long now = NowMs;
        if (!m.ThreatTable.TryGetValue(player, out var entry))
            m.ThreatTable[player] = entry = new MonsterUnit.ThreatRecord { DeclinedAtMs = now };
        entry.LastHitMs = now;
        return entry;
    }

    private bool MayHate(MonsterUnit m, GameSession player) =>
        player.Role != null && !player.Dead && !player.SceneLoading && MayEngage(m, player);

    /// <summary>More than the target's hatred and HatredExceedValue per 10000 of it (inferred reading, see HatredRules).</summary>
    private bool Exceeds(MonsterUnit m, long hatred)
    {
        long target = m.Target is { } t && m.ThreatTable.TryGetValue(t, out var held) ? held.Value : 0;
        return (Int128)hatred * BasisPoints > (Int128)target * (BasisPoints + HatredParams.ExceedBp);
    }

    private void TurnTo(MonsterUnit m, GameSession player)
    {
        SetTarget(m, player);
        m.Returning = false;
    }

    private static long Sum(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>On the fight tick: every entry declines, and one is dropped when its player is beyond HateClearDistance or
    /// it has not grown for HateClearTime (inferred). The target's is kept: the fight's own rules (its leash, a player gone)
    /// let the target go, as a boss whose leash reaches beyond 30 m keeps chasing a player out there.</summary>
    private void UpdateHatred(MonsterUnit m, long now)
    {
        var rules = HatredParams;
        // Dictionary allows Remove while it is enumerated.
        foreach (var (player, entry) in m.ThreatTable)
        {
            Decline(m.Template, entry, now);
            if (ReferenceEquals(player, m.Target)) continue;
            bool far = rules.ClearDistance > 0 && Distance(m.X, m.Z, player.Position.X, player.Position.Z) > rules.ClearDistance;
            bool stale = rules.ClearTimeMs > 0 && now - entry.LastHitMs >= rules.ClearTimeMs;
            if (far || stale) m.ThreatTable.Remove(player);
        }
    }

    /// <summary>
    /// AIConfig decline (inferred; no client code reads these fields): every _iHatredDeclineTime ms an entry loses
    /// _iHatredDeclineSpeed per 10000 of itself, at least _iMinHatredDecline, and stops at 0. The steps due are counted
    /// from the time that passed, whatever the tick. A share, because the one row with a minimum (1000, 5000 ms, 500) has
    /// the smallest rate, where a share of a small entry would come to almost nothing; a time of -1 or 0 never declines.
    /// </summary>
    internal static void Decline(MonsterTemplate t, MonsterUnit.ThreatRecord entry, long now)
    {
        int period = t.HatredDeclineTimeMs;
        if (period <= 0 || t.HatredDeclineSpeed <= 0 && t.MinHatredDecline <= 0) return;
        long steps = (now - entry.DeclinedAtMs) / period;
        if (steps <= 0) return;
        entry.DeclinedAtMs += steps * period;
        for (long step = 0; step < steps && entry.Value > 0; step++)
        {
            long decline = Math.Max((long)((Int128)entry.Value * t.HatredDeclineSpeed / BasisPoints), t.MinHatredDecline);
            if (decline <= 0) break;
            entry.Value = Math.Max(0, entry.Value - decline);
        }
    }
}
