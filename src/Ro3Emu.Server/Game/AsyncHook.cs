using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ro3Emu.Server.Diagnostics;

namespace Ro3Emu.Server.Game;

/// <summary>Snapshot entry describing one registered subscriber on a hook.</summary>
public readonly record struct HookSubscriber(int Order, string Name);

/// <summary>Implemented by hooks so tests and diagnostics can inspect subscriber registration order.</summary>
public interface IHookOrderSource
{
    IReadOnlyList<HookSubscriber> DescribeSubscribers();
}

/// <summary>Formats delegate targets into stable, human-readable names for logs, metrics, and order snapshots.</summary>
public static class HookDescriptor
{
    public static string Describe(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var method = handler.Method;
        var declaring = method.DeclaringType;
        if (declaring == null) return method.Name;

        bool compilerType = declaring.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || declaring.Name.Contains('<', StringComparison.Ordinal);
        if (!compilerType && !method.Name.Contains('<', StringComparison.Ordinal))
            return declaring.Name + "." + method.Name;

        var owner = declaring;
        while (owner.DeclaringType != null
            && (owner.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                || owner.Name.Contains('<', StringComparison.Ordinal)))
        {
            owner = owner.DeclaringType;
        }

        string methodName = method.Name;
        int open = methodName.IndexOf('<', StringComparison.Ordinal);
        int close = open >= 0 ? methodName.IndexOf('>', open + 1) : -1;
        if (open >= 0 && close > open + 1)
        {
            string enclosing = methodName[(open + 1)..close];
            string kind = methodName.Contains(">g__", StringComparison.Ordinal) ? "local" : "lambda";
            return owner.Name + ".<" + enclosing + "> " + kind;
        }

        return owner.Name + "." + methodName;
    }
}

internal static partial class HookLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Hook {Hook} subscriber {Subscriber} failed")]
    public static partial void SubscriberFailed(ILogger logger, string hook, string subscriber, Exception exception);
}

/// <summary>
/// Encapsulated, explicitly ordered hook list with immutable snapshot iteration and disposable subscriptions.
/// Subscribers run in ascending <c>order</c> (default 0); equal orders preserve registration sequence.
/// </summary>
public class OrderedHook<TDelegate>(
    string name = "",
    ILogger? logger = null,
    bool isolateErrors = false,
    ServerMetrics? metrics = null) : IHookOrderSource
    where TDelegate : Delegate
{
    private readonly Lock _gate = new();
    private ImmutableArray<Entry> _entries = [];
    private ImmutableArray<TDelegate> _handlers = [];
    private long _nextSequence;

    public string Name { get; } = name;
    public int Count => _entries.Length;

    /// <summary>Ordered snapshot of registered delegates for query or pipeline hooks.</summary>
    public ImmutableArray<TDelegate> Handlers => _handlers;

    protected ImmutableArray<Entry> Entries => _entries;
    protected bool IsolateErrors { get; } = isolateErrors;

    public IDisposable Subscribe(TDelegate handler, int order = 0, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        string subscriberName = string.IsNullOrWhiteSpace(name) ? HookDescriptor.Describe(handler) : name;

        long sequence;
        lock (_gate)
        {
            sequence = ++_nextSequence;
            var entry = new Entry(handler, order, sequence, subscriberName);
            int index = FindInsertIndex(_entries, order);
            _entries = _entries.Insert(index, entry);
            _handlers = _handlers.Insert(index, handler);
        }

        return new Subscription(this, sequence);
    }

    public IReadOnlyList<HookSubscriber> DescribeSubscribers()
    {
        var snapshot = _entries;
        if (snapshot.IsEmpty) return [];
        var list = new HookSubscriber[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
            list[i] = new HookSubscriber(snapshot[i].Order, snapshot[i].Name);
        return list;
    }

    protected void RecordFailure(Exception exception, string subscriberName)
    {
        if (logger != null)
            HookLog.SubscriberFailed(logger, string.IsNullOrEmpty(Name) ? "(unnamed)" : Name, subscriberName, exception);
        (metrics ?? ServerMetrics.LoopOwner)?.RecordHookError(Name, subscriberName);
    }

    private void Unsubscribe(long sequence)
    {
        lock (_gate)
        {
            var current = _entries;
            int index = -1;
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i].Sequence == sequence)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return;
            _entries = current.RemoveAt(index);
            _handlers = _handlers.RemoveAt(index);
        }
    }

    private static int FindInsertIndex(ImmutableArray<Entry> entries, int order)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Order > order) return i;
        }
        return entries.Length;
    }

    protected readonly record struct Entry(TDelegate Handler, int Order, long Sequence, string Name);

    private sealed class Subscription(OrderedHook<TDelegate> owner, long sequence) : IDisposable
    {
        private OrderedHook<TDelegate>? _owner = owner;

        public void Dispose()
        {
            var hook = Interlocked.Exchange(ref _owner, null);
            hook?.Unsubscribe(sequence);
        }
    }
}

public sealed class AsyncHook(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Func<Task>>(name, logger, isolateErrors, metrics)
{
    public async Task InvokeAsync()
    {
        foreach (var entry in Entries)
        {
            try { await entry.Handler(); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class AsyncHook<T1>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Func<T1, Task>>(name, logger, isolateErrors, metrics)
{
    public async Task InvokeAsync(T1 arg1)
    {
        foreach (var entry in Entries)
        {
            try { await entry.Handler(arg1); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class AsyncHook<T1, T2>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Func<T1, T2, Task>>(name, logger, isolateErrors, metrics)
{
    public async Task InvokeAsync(T1 arg1, T2 arg2)
    {
        foreach (var entry in Entries)
        {
            try { await entry.Handler(arg1, arg2); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class AsyncHook<T1, T2, T3>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Func<T1, T2, T3, Task>>(name, logger, isolateErrors, metrics)
{
    public async Task InvokeAsync(T1 arg1, T2 arg2, T3 arg3)
    {
        foreach (var entry in Entries)
        {
            try { await entry.Handler(arg1, arg2, arg3); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class AsyncHook<T1, T2, T3, T4>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Func<T1, T2, T3, T4, Task>>(name, logger, isolateErrors, metrics)
{
    public async Task InvokeAsync(T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        foreach (var entry in Entries)
        {
            try { await entry.Handler(arg1, arg2, arg3, arg4); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class AsyncHook<T1, T2, T3, T4, T5>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Func<T1, T2, T3, T4, T5, Task>>(name, logger, isolateErrors, metrics)
{
    public async Task InvokeAsync(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
    {
        foreach (var entry in Entries)
        {
            try { await entry.Handler(arg1, arg2, arg3, arg4, arg5); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class SyncHook(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Action>(name, logger, isolateErrors, metrics)
{
    public void Invoke()
    {
        foreach (var entry in Entries)
        {
            try { entry.Handler(); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class SyncHook<T1>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Action<T1>>(name, logger, isolateErrors, metrics)
{
    public void Invoke(T1 arg1)
    {
        foreach (var entry in Entries)
        {
            try { entry.Handler(arg1); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class SyncHook<T1, T2>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Action<T1, T2>>(name, logger, isolateErrors, metrics)
{
    public void Invoke(T1 arg1, T2 arg2)
    {
        foreach (var entry in Entries)
        {
            try { entry.Handler(arg1, arg2); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class SyncHook<T1, T2, T3>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Action<T1, T2, T3>>(name, logger, isolateErrors, metrics)
{
    public void Invoke(T1 arg1, T2 arg2, T3 arg3)
    {
        foreach (var entry in Entries)
        {
            try { entry.Handler(arg1, arg2, arg3); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}

public sealed class SyncHook<T1, T2, T3, T4>(string name = "", ILogger? logger = null, bool isolateErrors = false, ServerMetrics? metrics = null)
    : OrderedHook<Action<T1, T2, T3, T4>>(name, logger, isolateErrors, metrics)
{
    public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        foreach (var entry in Entries)
        {
            try { entry.Handler(arg1, arg2, arg3, arg4); }
            catch (Exception ex) when (IsolateErrors && ex is not OperationCanceledException) { RecordFailure(ex, entry.Name); }
        }
    }
}
