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
        if (_held.Values.Any(held => held.Any(a => ordered.Any(a.Overlaps))))
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
        return true;
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
        foreach (var held in _held.Values)
            foreach (var key in held)
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
        if (released.Length != 0) _lastRelease = _clock.GetTimestamp();
        // A runtime operation holds no keys: its release cannot unblock anyone, so it wakes no waiter.
        if (released.Length != 0) Signal();
    }
    private void Signal() { var changed = _changed; _changed = NewSignal(); changed.TrySetResult(); }
    /// <summary>An operation holding a key that overlaps <paramref name="keys"/>, and that key (diagnostics), or null.</summary>
    internal (long Operation, ReservationKey Key)? Blocker(IEnumerable<ReservationKey> keys)
    {
        foreach (var (operation, held) in _held)
            foreach (var key in held)
                if (keys.Any(key.Overlaps)) return (operation, key);
        return null;
    }
    internal bool Covers(long operation, IEnumerable<ReservationKey> keys) =>
        _held.TryGetValue(operation, out var held) && keys.All(key => held.Any(x => x.Covers(key)));
}
