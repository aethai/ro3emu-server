using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class AsyncHookTests
{
    [Fact]
    public async Task AsyncHookOrdersByPriorityAndPreservesRegistrationOrderOnTies()
    {
        var hook = new AsyncHook<List<string>>("Test.Ordered");
        hook.Subscribe(log => { log.Add("default-1"); return Task.CompletedTask; });
        hook.Subscribe(log => { log.Add("late"); return Task.CompletedTask; }, order: 10);
        hook.Subscribe(log => { log.Add("early"); return Task.CompletedTask; }, order: -10);
        hook.Subscribe(log => { log.Add("default-2"); return Task.CompletedTask; });
        hook.Subscribe(log => { log.Add("early-2"); return Task.CompletedTask; }, order: -10);

        List<string> calls = [];
        await hook.InvokeAsync(calls);

        Assert.Equal("early,early-2,default-1,default-2,late", string.Join(',', calls));
        Assert.Equal(5, hook.Count);
    }

    [Fact]
    public async Task DisposeUnsubscribesIdempotentlyAndSafelyDuringInvocation()
    {
        var hook = new AsyncHook<List<string>>("Test.Unsubscribe");
        IDisposable? removable = null;
        removable = hook.Subscribe(log =>
        {
            log.Add("first");
            removable?.Dispose();
            removable?.Dispose();
            return Task.CompletedTask;
        });
        hook.Subscribe(log =>
        {
            log.Add("second");
            return Task.CompletedTask;
        });

        List<string> firstRun = [];
        await hook.InvokeAsync(firstRun);
        Assert.Equal("first,second", string.Join(',', firstRun));

        List<string> secondRun = [];
        await hook.InvokeAsync(secondRun);
        Assert.Equal("second", string.Join(',', secondRun));
    }

    [Fact]
    public async Task AsyncHookPropagatesErrorsByDefault()
    {
        var hook = new AsyncHook("Test.Propagate");
        bool secondRan = false;
        hook.Subscribe(() => throw new InvalidOperationException("boom"));
        hook.Subscribe(() => { secondRan = true; return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => hook.InvokeAsync());
        Assert.False(secondRan);
    }

    [Fact]
    public async Task AsyncHookIsolatesErrorsLogsAndRecordsMetricsWhenEnabled()
    {
        var logger = new RecordingLogger();
        using var metrics = new ServerMetrics(observeRuntime: false);
        var hook = new AsyncHook<int>("Scene.Leaving", logger, isolateErrors: true, metrics: metrics);
        List<int> seen = [];

        hook.Subscribe(_ => throw new InvalidOperationException("broken"), order: -1, name: "BrokenSubscriber");
        hook.Subscribe(v => { seen.Add(v); return Task.CompletedTask; }, order: 1, name: "HealthySubscriber");

        await hook.InvokeAsync(42);

        Assert.Single(seen);
        Assert.Equal(42, seen[0]);
        Assert.Single(logger.Messages);
        Assert.Contains("Scene.Leaving", logger.Messages[0], StringComparison.Ordinal);
        Assert.Contains("BrokenSubscriber", logger.Messages[0], StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(metrics.Snapshot()));
        long recorded = doc.RootElement.GetProperty("errors").GetProperty("hookErrors")
            .GetProperty("Scene.Leaving/BrokenSubscriber").GetInt64();
        Assert.Equal(1, recorded);
    }

    [Fact]
    public async Task AsyncHookDoesNotSwallowOperationCanceledEvenWhenIsolating()
    {
        var hook = new AsyncHook("Test.Cancel", isolateErrors: true);
        hook.Subscribe(() => throw new OperationCanceledException("stopping"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => hook.InvokeAsync());
    }

    [Fact]
    public void SyncHookOrdersIsolatesAndRecordsMetrics()
    {
        var logger = new RecordingLogger();
        using var metrics = new ServerMetrics(observeRuntime: false);
        var hook = new SyncHook<string>("Combat.Dealt", logger, isolateErrors: true, metrics: metrics);
        List<string> calls = [];

        hook.Subscribe(v => calls.Add("second:" + v), order: 5, name: "Second");
        hook.Subscribe(_ => throw new InvalidOperationException("sync boom"), order: 0, name: "Boom");
        hook.Subscribe(v => calls.Add("first:" + v), order: -5, name: "First");

        hook.Invoke("hit");

        Assert.Equal("first:hit,second:hit", string.Join(',', calls));
        Assert.Single(logger.Messages);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(metrics.Snapshot()));
        long recorded = doc.RootElement.GetProperty("errors").GetProperty("hookErrors")
            .GetProperty("Combat.Dealt/Boom").GetInt64();
        Assert.Equal(1, recorded);
    }

    [Fact]
    public void OrderedHookExposesHandlersSnapshotInOrder()
    {
        var hook = new OrderedHook<Func<long, long>>("Combat.DamageAdjusters");
        hook.Subscribe(v => v + 10, order: 10, name: "AddTen");
        hook.Subscribe(v => v * 2, order: -10, name: "Double");

        long damage = 5;
        foreach (var adjuster in hook.Handlers) damage = adjuster(damage);

        Assert.Equal(20, damage);
        var described = hook.DescribeSubscribers();
        Assert.Equal(2, described.Count);
        Assert.Equal(new HookSubscriber(-10, "Double"), described[0]);
        Assert.Equal(new HookSubscriber(10, "AddTen"), described[1]);
    }

    [Fact]
    public void HookDescriptorFormatsMethodGroupsAndLambdasStably()
    {
        Action group = SampleHandler;
        Assert.Equal("AsyncHookTests.SampleHandler", HookDescriptor.Describe(group));

        int captured = 7;
        Func<int> lambda = () => captured;
        Assert.Equal("AsyncHookTests.<HookDescriptorFormatsMethodGroupsAndLambdasStably> lambda",
            HookDescriptor.Describe(lambda));
    }

    private static void SampleHandler() { }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
