namespace Ro3Emu.Server.Game.Ownership;

public enum ReservationKind { AccountAttachment, RoleLifecycle, RolePart, Realm, Entity }

public readonly record struct ReservationKey(ReservationKind Kind, string Id, string Part = "")
{
    public static ReservationKey Role(long id, string part = "WholeRole") => new(ReservationKind.RolePart, id.ToString(System.Globalization.CultureInfo.InvariantCulture), part);
    public static ReservationKey Lifecycle(long id) => new(ReservationKind.RoleLifecycle, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static ReservationKey Account(string id) => new(ReservationKind.AccountAttachment, id.ToUpperInvariant());
    public static ReservationKey Realm => new(ReservationKind.Realm, "legacy");
    public static ReservationKey Entity(string kind, string id, string part = "state") =>
        new(ReservationKind.Entity, kind + ":" + id, part);
    /// <summary>A reader's hold on an entity: shared holds coexist, any exclusive part of the entity excludes them,
    /// and an exclusive hold covers a nested shared request.</summary>
    public static ReservationKey SharedEntity(string kind, string id) => new(ReservationKind.Entity, kind + ":" + id, SharedPart);
    internal const string SharedPart = "shared";
    internal bool Overlaps(ReservationKey other)
    {
        if (Kind == ReservationKind.Realm || other.Kind == ReservationKind.Realm) return true;
        if (Kind == ReservationKind.Entity || other.Kind == ReservationKind.Entity)
            return Kind == other.Kind && Id == other.Id &&
                (Part == other.Part ? Part != SharedPart : Part == SharedPart || other.Part == SharedPart);
        if (Kind == ReservationKind.AccountAttachment || other.Kind == ReservationKind.AccountAttachment)
            return Kind == other.Kind && Id == other.Id;
        return Id == other.Id && (Kind == ReservationKind.RoleLifecycle || other.Kind == ReservationKind.RoleLifecycle ||
            Part == "WholeRole" || other.Part == "WholeRole" || Part == other.Part);
    }
    internal bool Covers(ReservationKey other) => Kind == ReservationKind.Realm || this == other ||
        Kind == ReservationKind.Entity && other.Kind == ReservationKind.Entity && Id == other.Id && other.Part == SharedPart ||
        Id == other.Id && Kind is ReservationKind.RoleLifecycle or ReservationKind.RolePart &&
        other.Kind is ReservationKind.RoleLifecycle or ReservationKind.RolePart && (Kind == ReservationKind.RoleLifecycle || Part == "WholeRole");
}

/// <summary>Loop-only, all-or-none reservations. Waiting never retains a partial key set.</summary>
public sealed class ReservationCoordinator(TimeProvider? clock = null)
{
    /// <summary>How long a realm request waits on its own before younger requests start waiting behind it.</summary>
    internal static readonly TimeSpan RealmPriorityAfter = TimeSpan.FromMilliseconds(250);

    /// <summary>With nothing released for this long, the realm priority stands aside (see <see cref="Pulse"/>).</summary>
    internal static readonly TimeSpan StallGrace = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<long, ReservationKey[]> _held = [];
    // The held keys by overlap domain (a role's parts and lifecycle, an account, an entity: keys of different domains never
    // overlap) and the realm keys held. A request scanned every held key of every operation, and woken waiters retry
    // together, so with thousands of players the loop spent nearly all its time here (LoadTest S3 at 2,000 bots: 95%).
    private readonly Dictionary<(int Domain, string Id), List<(long Operation, ReservationKey Key)>> _heldByDomain = [];
    private int _realmHolds, _heldKeys;
    // Realm priority: a realm request is admitted only when nothing at all is held, so on a busy realm the role work that
    // keeps arriving could hold it out for minutes (a live realm of 80 players: Revive waited 174 s). A realm request
    // that has waited RealmPriorityAfter makes every younger keyed request wait; the work already holding keys finishes
    // and the realm request gets in. Older requests and keyless runtime work are never held back.
    private readonly SortedDictionary<long, long> _realmWaiting = [];
    private long _lastRelease = (clock ?? TimeProvider.System).GetTimestamp();
    private bool _heldBack;
    private readonly Dictionary<string, ReservationKey[]> _recovery = [];
    // Writer preference on shared entities: an exclusive request blocked only by shared holds of an entity marks the
    // entity, and new shared requests for it then wait, so overlapping readers cannot starve the writer. A writer
    // blocked by anything else marks nothing: it would hold readers back behind an unrelated wait.
    private readonly Dictionary<long, string[]> _waitingWriters = [];
    private TaskCompletionSource _changed = NewSignal();
    // Per-domain wakeup signals: releasing a role or entity wakes only waiters blocked on that domain instead of
    // every waiting operation across the entire realm (eliminates O(N^2) thundering herd at 500-2000+ CCU).
    private readonly Dictionary<(int Domain, string Id), TaskCompletionSource> _domainSignals = [];
    public long Conflicts { get; private set; }
    public int Active => _held.Count;
    internal IReadOnlyDictionary<long, ReservationKey[]> Held => _held;
    internal int WaitingWriters => _waitingWriters.Count;
    public int RecoveryRequired => _recovery.Count;
    internal void Quarantine(string operation, IEnumerable<ReservationKey> keys) => _recovery[operation] = [.. keys.Distinct()];
    internal Task Changed => _changed.Task;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly Comparison<ReservationKey> KeyOrder = (a, b) =>
    {
        int c = a.Kind.CompareTo(b.Kind);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return c != 0 ? c : string.CompareOrdinal(a.Part, b.Part);
    };

    public bool TryReserve(long operation, IEnumerable<ReservationKey> keys)
    {
        if (_held.ContainsKey(operation)) throw new InvalidOperationException("Operation already reserved; pass its explicit owner context to nested work.");
        var ordered = keys.Distinct().ToArray();
        if (ordered.Length > 1) Array.Sort(ordered, KeyOrder);
        if (_recovery.Values.Any(held => held.Any(a => ordered.Any(a.Overlaps))))
            throw new InvalidOperationException("An overlapping committed owner operation requires recovery.");
        bool realm = ordered.Any(key => key.Kind == ReservationKind.Realm);
        if (HeldOverlaps(ordered))
        {
            Conflicts++;
            var starved = ReaderOnlyBlocks(ordered);
            if (starved.Length != 0) _waitingWriters[operation] = starved;
            else if (_waitingWriters.Remove(operation)) Signal();
            if (realm) _realmWaiting.TryAdd(operation, _clock.GetTimestamp());
            return false;
        }
        if (ordered.Length != 0 && BehindRealm(operation))
        {
            Conflicts++;
            RealmHoldBacks++;
            _heldBack = true;
            if (realm) _realmWaiting.TryAdd(operation, _clock.GetTimestamp());
            return false;
        }
        if (_waitingWriters.Count != 0 && ordered.Any(key => key.Kind == ReservationKind.Entity && key.Part == ReservationKey.SharedPart
            && _waitingWriters.Any(writer => writer.Key != operation && writer.Value.Contains(key.Id))))
        {
            Conflicts++;
            return false;
        }
        _waitingWriters.Remove(operation); // admitted: it holds the entity now, readers wait for its release
        _realmWaiting.Remove(operation);
        _held.Add(operation, ordered);
        Index(operation, ordered, add: true);
        return true;
    }

    private static (int Domain, string Id) Domain(ReservationKey key) => key.Kind switch
    {
        ReservationKind.RolePart or ReservationKind.RoleLifecycle => (0, key.Id),
        ReservationKind.AccountAttachment => (1, key.Id),
        _ => (2, key.Id), // Entity; Realm keys are counted, not indexed
    };

    private void Index(long operation, ReservationKey[] keys, bool add)
    {
        foreach (var key in keys)
        {
            _heldKeys += add ? 1 : -1;
            if (key.Kind == ReservationKind.Realm) { _realmHolds += add ? 1 : -1; continue; }
            var domain = Domain(key);
            if (add)
            {
                if (!_heldByDomain.TryGetValue(domain, out var list)) _heldByDomain[domain] = list = [];
                list.Add((operation, key));
            }
            else if (_heldByDomain.TryGetValue(domain, out var list))
            {
                list.Remove((operation, key));
                if (list.Count == 0) _heldByDomain.Remove(domain);
            }
        }
    }

    /// <summary>Whether a held key overlaps the request (<see cref="ReservationKey.Overlaps"/>): a realm request overlaps
    /// any held key, a held realm key any request, otherwise only keys of the same domain can.</summary>
    private bool HeldOverlaps(ReservationKey[] request)
    {
        if (request.Length == 0) return false; // keyless runtime work overlaps nothing, not even a realm hold
        if (request.Any(key => key.Kind == ReservationKind.Realm)) return _heldKeys > 0;
        if (_realmHolds > 0) return true;
        foreach (var key in request)
            if (_heldByDomain.TryGetValue(Domain(key), out var list))
                foreach (var (_, held) in list)
                    if (held.Overlaps(key)) return true;
        return false;
    }

    /// <summary>The held keys that can overlap the request, as (operation, key); every held key for a realm request or
    /// while a realm key is held.</summary>
    private IEnumerable<(long Operation, ReservationKey Key)> Candidates(IEnumerable<ReservationKey> request)
    {
        if (_realmHolds > 0 || request.Any(key => key.Kind == ReservationKind.Realm))
        {
            foreach (var (operation, held) in _held)
                foreach (var key in held) yield return (operation, key);
            yield break;
        }
        foreach (var key in request)
            if (_heldByDomain.TryGetValue(Domain(key), out var list))
                foreach (var pair in list) yield return pair;
    }
    /// <summary>A waiting request gave up (cancelled, or a one-shot try): its writer mark no longer holds readers back,
    /// nor does its realm priority.</summary>
    internal void Abandon(long operation)
    {
        if (_waitingWriters.Remove(operation) | _realmWaiting.Remove(operation)) Signal();
    }
    /// <summary>Requests a waiting realm request held back (diagnostics).</summary>
    public long RealmHoldBacks { get; private set; }
    internal int RealmWaiting => _realmWaiting.Count;

    // An older realm request has waited long enough: this one queues behind it, unless nothing has been released for
    // StallGrace. A holder that awaits younger work would otherwise wait for it forever while the realm request waits
    // for that holder; standing aside lets the younger work run and the holder finish.
    private bool BehindRealm(long operation)
    {
        if (_realmWaiting.Count == 0) return false;
        var (oldest, since) = _realmWaiting.First();
        return oldest < operation && _clock.GetElapsedTime(since) >= RealmPriorityAfter
            && _clock.GetElapsedTime(_lastRelease) < StallGrace;
    }

    /// <summary>Each loop tick (GameLoop): requests held back behind a realm request retry once nothing has been released
    /// for <see cref="StallGrace"/>, when the realm priority stands aside, since no release would wake them. True when it
    /// stood aside: some holder waited for work the priority held back (GameLoop logs the holders).</summary>
    internal bool Pulse()
    {
        if (_heldBack && _realmWaiting.Count != 0 && _clock.GetElapsedTime(_lastRelease) >= StallGrace)
        {
            _heldBack = false;
            Signal();
            return true;
        }
        return false;
    }
    // The entities whose exclusive keys in the request are blocked only by shared holds; empty when anything else blocks.
    private string[] ReaderOnlyBlocks(ReservationKey[] request)
    {
        var entities = new List<string>();
        foreach (var (_, key) in Candidates(request))
        {
            if (!request.Any(key.Overlaps)) continue;
            if (key.Kind != ReservationKind.Entity || key.Part != ReservationKey.SharedPart
                || !request.Any(r => r.Kind == ReservationKind.Entity && r.Id == key.Id && r.Part != ReservationKey.SharedPart))
                return [];
            if (!entities.Contains(key.Id)) entities.Add(key.Id);
        }
        return [.. entities];
    }
    public void Release(long operation)
    {
        if (!_held.Remove(operation, out var released)) throw new InvalidOperationException("Reservation released twice.");
        Index(operation, released, add: false);
        if (released.Length != 0) _lastRelease = _clock.GetTimestamp();
        // A runtime operation holds no keys: its release cannot unblock anyone, so it wakes no waiter.
        if (released.Length != 0) SignalReleased(released);
    }
    /// <summary>The task a waiter for <paramref name="keys"/> should await: single-domain waits subscribe only to
    /// their own domain when no realm hold/waiter is active; multi-key or realm waits use the global signal.</summary>
    internal Task SignalFor(ReservationKey[] keys)
    {
        if (keys.Length == 1 && keys[0].Kind != ReservationKind.Realm && _realmHolds == 0 && _realmWaiting.Count == 0)
        {
            var domain = Domain(keys[0]);
            if (!_domainSignals.TryGetValue(domain, out var tcs)) _domainSignals[domain] = tcs = NewSignal();
            return tcs.Task;
        }
        return _changed.Task;
    }
    private void SignalReleased(ReservationKey[] released)
    {
        bool anyRealm = false;
        foreach (var key in released)
        {
            if (key.Kind == ReservationKind.Realm) { anyRealm = true; break; }
            if (_domainSignals.Remove(Domain(key), out var domainTcs)) domainTcs.TrySetResult();
        }
        if (anyRealm) Signal();
        else
        {
            var changed = _changed;
            _changed = NewSignal();
            changed.TrySetResult();
        }
    }
    private void Signal()
    {
        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
        if (_domainSignals.Count > 0)
        {
            var pending = _domainSignals.Values.ToArray();
            _domainSignals.Clear();
            foreach (var tcs in pending) tcs.TrySetResult();
        }
    }
    /// <summary>An operation holding a key that overlaps <paramref name="keys"/>, and that key (diagnostics), or null.</summary>
    internal (long Operation, ReservationKey Key)? Blocker(IEnumerable<ReservationKey> keys)
    {
        var request = keys as ReservationKey[] ?? [.. keys];
        foreach (var (operation, key) in Candidates(request))
            if (request.Any(key.Overlaps)) return (operation, key);
        return null;
    }
    internal bool Covers(long operation, IEnumerable<ReservationKey> keys) =>
        _held.TryGetValue(operation, out var held) && keys.All(key => held.Any(x => x.Covers(key)));
}
