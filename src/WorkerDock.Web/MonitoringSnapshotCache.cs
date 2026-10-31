using WorkerDock.Application.Monitoring;

namespace WorkerDock.Web;

public sealed class MonitoringSnapshotCache(
    IMonitoringQueries queries,
    TimeProvider clock,
    IHostApplicationLifetime lifetime)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly CancellationToken _stopping = lifetime.ApplicationStopping;
    private readonly Snapshot<DashboardDto> _dashboard = new();
    private readonly Snapshot<RuntimeStatusDto> _workers = new();

    public Task<DashboardDto> DashboardAsync(CancellationToken requestCancellation) =>
        GetAsync(_dashboard, queries.DashboardAsync, requestCancellation);

    public Task<RuntimeStatusDto> WorkersAsync(CancellationToken requestCancellation) =>
        GetAsync(_workers, queries.WorkersAsync, requestCancellation);

    private Task<T> GetAsync<T>(Snapshot<T> snapshot, Func<CancellationToken, ValueTask<T>> query, CancellationToken requestCancellation)
    {
        TaskCompletionSource<T>? start = null;
        Task<T> shared;
        lock (_gate)
        {
            if (snapshot.HasValue && clock.GetElapsedTime(snapshot.CompletedAt, clock.GetTimestamp()) < Lifetime)
                return Task.FromResult(snapshot.Value!).WaitAsync(requestCancellation);

            if (snapshot.InFlight is not null)
            {
                shared = snapshot.InFlight;
            }
            else
            {
                start = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = start.Task;
                snapshot.InFlight = shared;
            }
        }

        if (start is not null)
            _ = PopulateAsync(snapshot, query, start);

        return shared.WaitAsync(requestCancellation);
    }

    private async Task PopulateAsync<T>(Snapshot<T> snapshot, Func<CancellationToken, ValueTask<T>> query, TaskCompletionSource<T> completion)
    {
        try
        {
            var value = await query(_stopping).ConfigureAwait(false);
            var completedAt = clock.GetTimestamp();
            lock (_gate)
            {
                snapshot.Value = value;
                snapshot.CompletedAt = completedAt;
                snapshot.HasValue = true;
                snapshot.InFlight = null;
            }
            completion.TrySetResult(value);
        }
        catch (Exception exception)
        {
            lock (_gate)
                snapshot.InFlight = null;
            completion.TrySetException(exception);
        }
    }

    private sealed class Snapshot<T>
    {
        public bool HasValue { get; set; }
        public T? Value { get; set; }
        public long CompletedAt { get; set; }
        public Task<T>? InFlight { get; set; }
    }
}
