using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>The coordinator finds blocking holds through its per-domain index; every answer must equal a scan of every
/// held key (the rule it replaced), whatever mix of role, lifecycle, account, entity and realm keys is held.</summary>
public sealed class ReservationIndexTests
{
    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 1_000_000; // no realm priority ever comes due
    }

    [Fact]
    public void IndexedAdmissionMatchesAScanOfEveryHeldKey()
    {
        var random = new Random(484);
        var reservations = new ReservationCoordinator(new FrozenClock());
        string[] parts = ["WholeRole", "Bag", "Skills"];
        ReservationKey RandomKey() => random.Next(20) switch
        {
            0 => ReservationKey.Realm,
            < 8 => ReservationKey.Role(random.Next(6), parts[random.Next(parts.Length)]),
            < 11 => ReservationKey.Lifecycle(random.Next(6)),
            < 15 => ReservationKey.Account("a" + random.Next(4)),
            _ => ReservationKey.Entity("guild", random.Next(4).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var held = new List<long>();
        long next = 1;
        for (int step = 0; step < 20_000; step++)
        {
            if (held.Count > 0 && random.Next(10) < 4)
            {
                int i = random.Next(held.Count);
                reservations.Release(held[i]);
                held.RemoveAt(i);
                continue;
            }
            // Zero keys: keyless runtime work, which no hold may block.
            var request = Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomKey()).Distinct().ToArray();
            bool blocked = reservations.Held.Values.Any(keys => keys.Any(key => request.Any(key.Overlaps)));
            long operation = next++;
            Assert.Equal(!blocked, reservations.TryReserve(operation, request));
            if (!blocked) held.Add(operation);
            else reservations.Abandon(operation);
        }
    }
}
