using Microsoft.Extensions.Hosting;
using WorkerDock.Application.Monitoring;
using WorkerDock.Web;

namespace WorkerDock.IntegrationTests;

public sealed class MonitoringSnapshotCacheTests
{
    [Fact]
    public async Task Dashboard_value_is_cached_until_five_seconds_then_refreshed()
    {
        var queries = new StubQueries();
        var cache = CreateCache(queries, out var clock);

        var first = await cache.DashboardAsync(default);
        clock.Advance(TimeSpan.FromSeconds(4.999));
        Assert.Same(first, await cache.DashboardAsync(default));
        Assert.Equal(1, queries.DashboardCalls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.NotSame(first, await cache.DashboardAsync(default));
        Assert.Equal(2, queries.DashboardCalls);
    }

    [Fact]
    public async Task Dashboard_and_workers_have_independent_snapshots()
    {
        var queries = new StubQueries();
        var cache = CreateCache(queries, out _);

        await cache.DashboardAsync(default);
        await cache.WorkersAsync(default);
        await cache.DashboardAsync(default);
        await cache.WorkersAsync(default);

        Assert.Equal(1, queries.DashboardCalls);
        Assert.Equal(1, queries.WorkersCalls);
    }

    [Fact]
    public async Task Fifty_concurrent_dashboard_callers_share_one_query()
    {
        var gate = new TaskCompletionSource<DashboardDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new StubQueries { Dashboard = _ => new ValueTask<DashboardDto>(gate.Task) };
        var cache = CreateCache(queries, out _);

        var callers = Enumerable.Range(0, 50).Select(_ => cache.DashboardAsync(default)).ToArray();
        Assert.Equal(1, queries.DashboardCalls);
        var value = CreateDashboard();
        gate.SetResult(value);

        var results = await Task.WhenAll(callers);
        Assert.All(results, result => Assert.Same(value, result));
    }

    [Fact]
    public async Task Cancelling_one_waiter_does_not_cancel_shared_query_or_other_waiters()
    {
        var gate = new TaskCompletionSource<DashboardDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken queryToken = default;
        var queries = new StubQueries { Dashboard = ct => { queryToken = ct; return new ValueTask<DashboardDto>(gate.Task); } };
        var cache = CreateCache(queries, out _);
        using var cts = new CancellationTokenSource();
        var cancelledWaiter = cache.DashboardAsync(cts.Token);
        var survivingWaiter = cache.DashboardAsync(default);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        Assert.False(queryToken.IsCancellationRequested);
        var value = CreateDashboard();
        gate.SetResult(value);
        Assert.Same(value, await survivingWaiter);
        Assert.Equal(1, queries.DashboardCalls);
    }

    [Fact]
    public async Task Failed_query_is_retried_and_expired_value_is_never_served_after_failure()
    {
        var fail = false;
        var queries = new StubQueries { Dashboard = _ => fail
            ? ValueTask.FromException<DashboardDto>(new InvalidOperationException("query failed"))
            : new ValueTask<DashboardDto>(CreateDashboard()) };
        var cache = CreateCache(queries, out var clock);
        var value = await cache.DashboardAsync(default);
        fail = true;
        clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.DashboardAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.DashboardAsync(default));
        Assert.Equal(3, queries.DashboardCalls);
        fail = false;
        Assert.NotSame(value, await cache.DashboardAsync(default));
    }

    [Fact]
    public async Task Initial_failure_is_not_cached_and_next_call_retries()
    {
        var fail = true;
        var queries = new StubQueries { Workers = _ => fail
            ? ValueTask.FromException<RuntimeStatusDto>(new InvalidOperationException("query failed"))
            : new ValueTask<RuntimeStatusDto>(new RuntimeStatusDto(Guid.NewGuid(), true, 4, DateTimeOffset.UtcNow)) };
        var cache = CreateCache(queries, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.WorkersAsync(default));
        fail = false;
        Assert.True((await cache.WorkersAsync(default)).Ready);
        Assert.Equal(2, queries.WorkersCalls);
    }

    private static MonitoringSnapshotCache CreateCache(StubQueries queries, out ManualTimeProvider clock)
    {
        clock = new ManualTimeProvider();
        return new MonitoringSnapshotCache(queries, clock, new TestLifetime());
    }

    private static DashboardDto CreateDashboard() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Healthy", 0, 0, 0, 0, 0, 0, 0, true, 4, null, [], []);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class StubQueries : IMonitoringQueries
    {
        public int DashboardCalls;
        public int WorkersCalls;
        public Func<CancellationToken, ValueTask<DashboardDto>> Dashboard { get; set; } = _ => new(CreateDashboard());
        public Func<CancellationToken, ValueTask<RuntimeStatusDto>> Workers { get; set; } = _ => new(new RuntimeStatusDto(Guid.NewGuid(), true, 4, DateTimeOffset.UtcNow));
        public ValueTask<DashboardDto> DashboardAsync(CancellationToken ct) { Interlocked.Increment(ref DashboardCalls); return Dashboard(ct); }
        public ValueTask<RuntimeStatusDto> WorkersAsync(CancellationToken ct) { Interlocked.Increment(ref WorkersCalls); return Workers(ct); }
        public ValueTask<Page<JobSummaryDto>> JobsAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Page<ExecutionHistoryItemDto>> ExecutionsAsync(ExecutionHistoryFilter filter, PageRequest page, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ExecutionDetailsDto> ExecutionAsync(Guid executionId, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<Page<AuditSummaryDto>> AuditAsync(AuditHistoryFilter filter, PageRequest page, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> AuditEventTypesAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<TenantHealthDto> HealthAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}







