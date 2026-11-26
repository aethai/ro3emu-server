using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One connection and one dedicated thread. Admission accounts for both queued and executing plans.</summary>
public sealed partial class DbWriter : IDisposable
{
    public const int MaxDurableJobs = 1024, MaxDurableBytes = 4 * 1024 * 1024, MaxLazyBytes = 4 * 1024 * 1024;
    private const int MaxLazyJobs = 1024;
    private readonly object _gate = new();
    private readonly Queue<Job> _durable = new(), _lazy = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _connectionString;
    private readonly ServerMetrics? _serverMetrics;
    private readonly DbAccessMetrics _metrics;
    private readonly ILogger? _log;
    private readonly TimeSpan _stopTimeout;
    private Job? _active;
    private IReadOnlyList<Pending>? _failedTerminalGroup;
    private int _durableJobs, _durableBytes, _lazyJobs, _lazyBytes;
    private bool _stopping, _lastWasOverdueLazy;
    private Exception? _terminalError;
    /// <summary>Fairness limit: a lazy job older than this alternates with durable work instead of waiting for an
    /// idle writer, so sustained durable load cannot starve lazy parts (position/settings).</summary>
    internal TimeSpan LazyMaxWait { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Bounded backoff for transient lock-contention commit errors where SQLite guarantees rollback (BUSY/LOCKED).</summary>
    internal TimeSpan[] CommitRetryDelays { get; set; } =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)];

    private sealed record Job(DbPlan Plan, bool Lazy, TaskCompletionSource<object?> Done, long EnqueuedAt);

    public DbWriter(string connectionString, DbAccessMetrics metrics, ServerMetrics? serverMetrics = null, ILogger? log = null,
        TimeSpan? stopTimeout = null)
    {
        _connectionString = connectionString;
        _metrics = metrics;
        _serverMetrics = serverMetrics;
        _log = log;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _thread = new Thread(Run) { IsBackground = true, Name = "ro3-db-writer" };
        using (ExecutionContext.SuppressFlow()) _thread.Start();
    }

    public Task WorkerExited => _exited.Task;
    // Test-only deterministic barrier after SQL/revision work and before FULL commit.
    internal Action<string>? BeforeCommitForTest { get; set; }
    internal Action? BeforeGroupCommitForTest { get; set; }
    internal Action<string>? AfterCommitForTest { get; set; }
    /// <summary>Invoked when the writer enters terminal failure so the host can initiate clean shutdown.</summary>
    internal Action<Exception>? TerminalFault { get; init; }
    /// <summary>Runs on this thread right after COMMIT with the role parts the plan declares (snapshot invalidation).</summary>
    internal Action<IReadOnlyList<RoleRevisionKey>>? RoleWritesCommitted { get; set; }
    internal bool StopRequested { get { lock (_gate) return _stopping; } }
    public bool IsTerminalFailed { get { lock (_gate) return _terminalError != null; } }

    public Task<T> ExecuteAsync<T>(DbPlan plan, bool lazy = false) => Cast<T>(Enqueue(plan, lazy));

    private Task<object?> Enqueue(DbPlan plan, bool lazy)
    {
        if (plan.Bytes < 0) throw new ArgumentOutOfRangeException(nameof(plan));
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping)
            {
                _metrics.WriterRefused();
                return Task.FromException<object?>(_terminalError is { } error
                    ? new InvalidOperationException("Database writer failed and cannot admit more work.", error)
                    : new ObjectDisposedException(nameof(DbWriter)));
            }
            if (lazy ? _lazyJobs >= MaxLazyJobs || (long)_lazyBytes + plan.Bytes > MaxLazyBytes
                : _durableJobs >= MaxDurableJobs || (long)_durableBytes + plan.Bytes > MaxDurableBytes)
            {
                _metrics.WriterRefused();
                return Task.FromException<object?>(new DbQueueFullException("Database writer admission closed or full."));
            }
            if (lazy) { _lazyJobs++; _lazyBytes += plan.Bytes; _lazy.Enqueue(new(plan, true, done, Stopwatch.GetTimestamp())); }
            else { _durableJobs++; _durableBytes += plan.Bytes; _durable.Enqueue(new(plan, false, done, Stopwatch.GetTimestamp())); }
            _metrics.WriterAdmitted(lazy, plan.Bytes);
            Monitor.Pulse(_gate);
        }
        return done.Task;
    }

    private static async Task<T> Cast<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;

    // Caches a plan reports stale while it executes on this writer thread; this writer's database bumps them when the
    // job ends, committed or not (an extra invalidation is harmless), before the job's completion reaches its caller.
    [ThreadStatic] private static List<WrittenCache>? _afterJob;
    /// <summary>For a plan's Execute: the job writes rows behind <paramref name="cache"/>.</summary>
    internal static void AfterJob(WrittenCache cache) => (_afterJob ??= []).Add(cache);

    // A plan's instance fields are part of its receipt fingerprint, so a plan reports through this writer thread instead.
    [ThreadStatic] private static DbWriter? _onThread;
    /// <summary>Receives the stored rows the economy decoder could not read (<see cref="ReportStandingIssue"/>).</summary>
    internal Action<EconomyIssue>? StandingIssue { get; init; }
    /// <summary>For a plan's Execute: a stored row it found unreadable, left out of its economy checks.</summary>
    internal static void ReportStandingIssue(EconomyIssue issue) => _onThread?.StandingIssue?.Invoke(issue);

    /// <summary>The cache generations of this writer's database (its <see cref="AccountStore"/>).</summary>
    public CacheGenerations Caches { get; init; } = new();

    public T ExecuteLegacySync<T>(DbPlan plan, bool onLoop)
    {
        long started = Stopwatch.GetTimestamp();
        // The blocked caller observes worker completion directly; async continuations stay asynchronous.
        try { return (T)Enqueue(plan, lazy: false).GetAwaiter().GetResult()!; }
        finally { _metrics.LegacyWait(started, onLoop, writer: true, label: plan.Label); }
    }

    private void Run()
    {
        _onThread = this;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Pooling = false }.ToString());
            using (var measurement = _serverMetrics?.MeasureDatabaseOpen()) db.Open();
            SqliteMetrics.Attach(db, _serverMetrics);
            DbWriteGuard.AttachWriter(db, _metrics);
            string syncMode = string.Equals(Environment.GetEnvironmentVariable("RO3EMU_DB_SYNCHRONOUS"), "NORMAL", StringComparison.OrdinalIgnoreCase)
                ? "NORMAL" : "FULL";
            using (var full = db.CreateCommand()) { full.CommandText = $"PRAGMA synchronous={syncMode}; PRAGMA cache_size=-32000; PRAGMA temp_store=MEMORY;"; full.ExecuteNonQuery(); }
            _metrics.WriterThread(Environment.CurrentManagedThreadId);
            while (true)
            {
                Job first;
                lock (_gate)
                {
                    while (_durable.Count == 0 && _lazy.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    if (_durable.Count == 0 && _lazy.Count == 0) break;
                    first = NextJob();
                    _active = first;
                }
                RunGroup(db, first);
            }
        }
        catch (Exception error)
        {
            _metrics.WriterTerminal();
            _log?.LogCritical(error, "Database writer worker failed; queued plans will fail and no write is admitted");
            List<Job> droppedDurable, droppedLazy;
            lock (_gate)
            {
                _stopping = true; _terminalError = error;
                droppedDurable = [.. _durable];
                droppedLazy = [.. _lazy];
                foreach (var job in droppedDurable.Concat(droppedLazy))
                {
                    job.Done.TrySetException(error);
                    _metrics.WriterFinished(job.Lazy, job.Plan.Bytes, false);
                }
                _durable.Clear(); _lazy.Clear();
                _durableJobs = _durableBytes = _lazyJobs = _lazyBytes = 0;
            }
            LogDroppedTerminalWork(droppedDurable, droppedLazy);
            if (TerminalFault is { } onTerminal)
            {
                try { onTerminal(error); }
                catch (Exception callbackError) { if (_log != null) LogTerminalFaultCallbackFailed(_log, callbackError); }
            }
        }
        finally { _exited.TrySetResult(); }
    }

    private void LogDroppedTerminalWork(List<Job> droppedDurable, List<Job> droppedLazy)
    {
        if (_log == null) return;
        var failedJobs = _failedTerminalGroup?.Where(p => p.Error != null).Select(p => p.Job).ToList() ?? [];
        _failedTerminalGroup = null;
        var lost = failedJobs.Concat(droppedDurable).Concat(droppedLazy).ToList();
        if (lost.Count == 0) return;
        long totalBytes = lost.Sum(j => (long)j.Plan.Bytes);
        string labels = string.Join(", ", lost.Select(j => j.Plan.Label).Distinct(StringComparer.Ordinal).Take(12));
        var roles = lost.SelectMany(j => j.Plan.RoleWrites).Select(r => r.RoleId).Distinct().Order().Take(20)
            .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        string roleList = roles.Length == 0 ? "none" : string.Join(", ", roles);
        LogTerminalPendingWorkLost(_log, failedJobs.Count, droppedDurable.Count, droppedLazy.Count, totalBytes, labels, roleList);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Terminal writer fault callback failed")]
    private static partial void LogTerminalFaultCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database commit rolled back with SQLite error {ErrorCode}; retrying {Count} job(s) (attempt {Attempt}/{MaxAttempts})")]
    private static partial void LogCommitRetry(ILogger logger, int errorCode, int count, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Uncommitted writes lost on terminal database writer failure: failedGroup={FailedGroup}, "
            + "droppedDurable={DroppedDurable}, droppedLazy={DroppedLazy}, bytes={Bytes}, labels=[{Labels}], roles=[{Roles}]")]
    private static partial void LogTerminalPendingWorkLost(ILogger logger, int failedGroup, int droppedDurable, int droppedLazy, long bytes, string labels, string roles);

    // Group commit: jobs already queued behind the first one join its transaction, each inside its own
    // savepoint (a failing job rolls back only itself), and one FULL commit makes the group durable. Every job's
    // completion is still released only after that commit, so durability and per-job outcomes are unchanged; jobs
    // execute in the same order as before, each seeing the earlier ones' writes. On the realm's disk a commit was
    // about 6 ms and a new role's login a dozen of them, one after another: the writer queue, not the loop, was what
    // a login storm waited on.
    internal const int MaxGroupJobs = 64;
    internal TimeSpan MaxGroupExecution { get; set; } = TimeSpan.FromMilliseconds(25);

    private sealed class Pending(Job job)
    {
        public readonly Job Job = job;
        public object? Result;
        public Exception? Error;
        public bool ReplayValidated, Executed;
        public List<WrittenCache>? AfterJob;
    }

    private void RunGroup(SqliteConnection db, Job first)
    {
        var group = new List<Pending>();
        Exception? groupError = null;
        bool commitFailed = false;
        long groupStarted = Stopwatch.GetTimestamp();
        SqliteTransaction? tx = null;
        try
        {
            tx = db.BeginTransaction();
            var job = first;
            while (true)
            {
                var pending = new Pending(job);
                group.Add(pending);
                _metrics.WriterDequeued(job.EnqueuedAt);
                if (!Execute(db, tx, pending, "g" + group.Count, out groupError)) break;
                lock (_gate)
                {
                    if (group.Count >= MaxGroupJobs || Stopwatch.GetElapsedTime(groupStarted) > MaxGroupExecution
                        || _durable.Count == 0 && _lazy.Count == 0) break;
                    job = NextJob();
                    _active = job;
                }
            }
            if (groupError == null)
                (groupError, commitFailed) = CommitOrRollback(tx, group.Count(p => p.Executed));
        }
        catch (Exception e)
        {
            groupError ??= e;
            if (group.Count == 0) group.Add(new Pending(first)); // BEGIN itself failed: the job still completes
        }
        finally { tx?.Dispose(); }
        if (commitFailed && groupError is { } retryable && IsSafeRollbackRetryError(retryable))
            (groupError, commitFailed) = RetryCommittedGroup(db, group, retryable);
        foreach (var pending in group) Finish(db, pending, groupError);
        if (groupError != null && (commitFailed || IsTerminalStorageError(groupError)))
        {
            _failedTerminalGroup = group;
            throw groupError;
        }
    }

    private (Exception? Error, bool CommitFailed) CommitOrRollback(SqliteTransaction tx, int executed)
    {
        if (executed == 0)
        {
            tx.Rollback();
            return (null, false);
        }
        try
        {
            BeforeGroupCommitForTest?.Invoke();
            tx.Commit();
            _metrics.WriterCommitted(executed);
            return (null, false);
        }
        catch (Exception commitError)
        {
            return (commitError, true);
        }
    }

    private (Exception? Error, bool CommitFailed) RetryCommittedGroup(SqliteConnection db, List<Pending> group, Exception initialError)
    {
        var toRetry = new List<int>(group.Count);
        for (int i = 0; i < group.Count; i++)
            if (group[i].Executed) toRetry.Add(i);
        if (toRetry.Count == 0) return (initialError, true);

        Exception currentError = initialError;
        var delays = CommitRetryDelays;
        for (int attempt = 1; attempt <= delays.Length; attempt++)
        {
            int code = currentError is SqliteException se ? se.SqliteErrorCode : 0;
            if (_log != null) LogCommitRetry(_log, code, toRetry.Count, attempt, delays.Length);
            if (delays[attempt - 1] > TimeSpan.Zero) Thread.Sleep(delays[attempt - 1]);

            var (retryError, retryCommitFailed) = TryReplayGroupOnce(db, group, toRetry);
            if (retryError == null) return (null, false);
            currentError = retryError;
            if (!retryCommitFailed || !IsSafeRollbackRetryError(retryError))
                return (retryError, retryCommitFailed);
        }
        return (currentError, true);
    }

    private (Exception? Error, bool CommitFailed) TryReplayGroupOnce(SqliteConnection db, List<Pending> group, List<int> indices)
    {
        Exception? retryError = null;
        bool retryCommitFailed = false;
        SqliteTransaction? retryTx = null;
        try
        {
            retryTx = db.BeginTransaction();
            foreach (int idx in indices)
            {
                var fresh = new Pending(group[idx].Job);
                group[idx] = fresh;
                if (!Execute(db, retryTx, fresh, "g" + (idx + 1).ToString(CultureInfo.InvariantCulture), out retryError))
                    break;
            }
            if (retryError == null)
                (retryError, retryCommitFailed) = CommitOrRollback(retryTx, indices.Count(i => group[i].Executed));
        }
        catch (Exception e) { retryError ??= e; }
        finally { retryTx?.Dispose(); }
        return (retryError, retryCommitFailed);
    }

    // Only retry transient lock-contention errors where SQLite guarantees rollback (BUSY=5, LOCKED=6).
    // Terminal storage errors (SQLITE_FULL=13) and ambiguous I/O errors (IOException, SQLITE_IOERR=10) are never retried.
    private static bool IsSafeRollbackRetryError(Exception error) =>
        error is SqliteException { SqliteErrorCode: 5 or 6 };

    private static bool IsTerminalStorageError(Exception error) =>
        error is SqliteException { SqliteErrorCode: 8 or 10 or 11 or 13 or 14 or 26 } or IOException;

    /// <summary>One job inside the group's transaction. False when the transaction itself is lost (a failed
    /// savepoint rollback): the group then fails as a whole.</summary>
    private bool Execute(SqliteConnection db, SqliteTransaction tx, Pending pending, string savepoint, out Exception? groupError)
    {
        groupError = null;
        var plan = pending.Job.Plan;
        long executionStarted = Stopwatch.GetTimestamp();
        bool open = false;
        try
        {
            DbWriteGuard.Plan(db, plan);
            tx.Save(savepoint);
            open = true;
            // An economy envelope replays its own receipt; other owned operations replay the generic one.
            bool replay = plan.TryReplay(db, tx, out pending.Result);
            pending.ReplayValidated = true;
            if (!replay
                && (plan.OwnsOperationReceipt || plan.OperationId is not { } id ||
                    !OperationReceipts.TryRead(db, tx, id, out pending.Result)))
            {
                var deltaRevisions = RevisionStore.Check(db, tx, plan);
                pending.Result = plan.Execute(db, tx);
                RevisionStore.Bump(db, tx, plan, pending.Result, deltaRevisions);
                if (plan.OperationId != null && !plan.OwnsOperationReceipt)
                    OperationReceipts.Save(db, tx, plan, pending.Result);
            }
            DbWriteGuard.AssertWriter(db);
            BeforeCommitForTest?.Invoke(plan.Label);
            tx.Release(savepoint);
            open = false;
            pending.Executed = true;
        }
        catch (Exception caught)
        {
            if (caught is OwnerRevisionConflictException) _metrics.RevisionConflict();
            pending.Error = caught;
            if (open)
            {
                try { tx.Rollback(savepoint); tx.Release(savepoint); }
                catch (Exception lost) { groupError = lost; }
            }
        }
        finally
        {
            // Invalidations a plan registered run after the group's commit (or its rollback), before its completion.
            if (_afterJob is { Count: > 0 } afterJob) { pending.AfterJob = [.. afterJob]; afterJob.Clear(); }
            _metrics.WriterExecuted(Stopwatch.GetTimestamp() - executionStarted);
            DbWriteGuard.Plan(db, "writer/idle");
        }
        return groupError == null;
    }

    private void Finish(SqliteConnection db, Pending pending, Exception? groupError)
    {
        var job = pending.Job;
        bool success = false;
        try
        {
            // An executed job of a group that did not commit failed with the group.
            if (pending.Error == null && groupError != null) pending.Error = groupError;
            if (pending.Error == null)
            {
                if (job.Plan.RoleWrites.Count != 0) InvalidateCommitted(job.Plan);
                try
                {
                    if (job.Plan.OperationId != null) AfterCommitForTest?.Invoke(job.Plan.Label);
                    success = true;
                    job.Plan.AfterCommit();
                }
                catch (Exception caught) { success = false; pending.Error = caught; }
            }
            if (pending.Error is { } caughtError && pending.ReplayValidated && !job.Plan.OwnsOperationReceipt &&
                caughtError is not Economy.EconomyOperationConflictException &&
                !(job.Plan is EventEconomyTransactionPlan && caughtError is InvalidDataException) &&
                job.Plan.OperationId is { } operationId)
            {
                try
                {
                    // Resolve from what is committed, on a fresh read-only connection of this worker, before
                    // releasing the accepted job's completion.
                    using var recovery = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString)
                    { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    recovery.Open();
                    DbWriteGuard.AttachRead(recovery, _metrics, "writer/resolve-operation");
                    if (OperationReceipts.TryRead(recovery, null, operationId, out var receipt))
                    { pending.Result = receipt; pending.Error = null; success = true; }
                }
                catch (Exception recoveryError) { pending.Error = new OwnerOutcomeUnknownException(operationId, recoveryError); }
            }
        }
        finally
        {
            if (pending.AfterJob is { } afterJob)
                foreach (var cache in afterJob) Caches.Bump(cache);
            lock (_gate)
            {
                if (ReferenceEquals(_active, job)) _active = null;
                if (job.Lazy) { _lazyJobs--; _lazyBytes -= job.Plan.Bytes; }
                else { _durableJobs--; _durableBytes -= job.Plan.Bytes; }
            }
            _metrics.WriterFinished(job.Lazy, job.Plan.Bytes, success);
        }
        if (pending.Error is null) job.Done.TrySetResult(pending.Result);
        else job.Done.TrySetException(pending.Error);
    }

    /// <summary>
    /// The writer's own post-commit step (snapshot invalidation). The write is durable once COMMIT returned, so a
    /// failure here must not report it as failed - its caller would retry an applied write. Plan-defined steps
    /// (<see cref="DbPlan.AfterCommit"/>) keep their semantics: receipt-backed plans use them as a lost acknowledgement.
    /// </summary>
    private void InvalidateCommitted(DbPlan plan)
    {
        try { RoleWritesCommitted?.Invoke(plan.RoleWrites); }
        catch (Exception e)
        {
            _metrics.PostCommitFailure();
            _log?.LogCritical(e, "Snapshot invalidation after the commit of {Label} failed", plan.Label);
        }
    }

    /// <summary>Durable first; an overdue lazy job alternates with durable ones (never two overdue in a row while
    /// durable work waits). Called under the gate with at least one job queued.</summary>
    private Job NextJob()
    {
        if (_durable.Count == 0) { _lastWasOverdueLazy = false; return _lazy.Dequeue(); }
        if (_lazy.Count != 0 && !_lastWasOverdueLazy && Stopwatch.GetElapsedTime(_lazy.Peek().EnqueuedAt) > LazyMaxWait)
        {
            _lastWasOverdueLazy = true;
            _metrics.LazyPromoted();
            return _lazy.Dequeue();
        }
        _lastWasOverdueLazy = false;
        return _durable.Dequeue();
    }

    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
        // The worker closes its own connection after accepted work. The caller never closes an in-use handle.
        if (Thread.CurrentThread != _thread && !_exited.Task.Wait(_stopTimeout))
        {
            _metrics.WorkerStopTimedOut(writer: true);
            (int durableJobs, int durableBytes, int lazyJobs, int lazyBytes, string labels) debt;
            lock (_gate)
                debt = (_durableJobs, _durableBytes, _lazyJobs, _lazyBytes,
                    string.Join(",", (_active is null ? Enumerable.Empty<DbPlan>() : [_active.Plan])
                        .Concat(_durable.Select(x => x.Plan)).Concat(_lazy.Select(x => x.Plan))
                        .Select(x => x.Label.Length > 64 ? x.Label[..64] : x.Label).Distinct().Take(8)));
            _log?.LogError("Database writer shutdown timed out with durableJobs={DurableJobs}, durableBytes={DurableBytes}, "
                + "lazyJobs={LazyJobs}, lazyBytes={LazyBytes}, labels={Labels}; worker retains its connection until accepted plans finish",
                debt.durableJobs, debt.durableBytes, debt.lazyJobs, debt.lazyBytes, debt.labels);
        }
    }
}

/// <summary>The writer or reader did not admit the plan (full or closed): nothing of it ran, so the caller's operation
/// definitely failed and needs no recovery.</summary>
public sealed class DbQueueFullException(string message) : PersistenceException(message);
