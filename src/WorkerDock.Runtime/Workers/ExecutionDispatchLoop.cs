using System.Collections.Concurrent;
using WorkerDock.Contracts.Execution;
using WorkerDock.Application.Executions;
using WorkerDock.Runtime.Ownership;

namespace WorkerDock.Runtime.Workers;

/// <summary>Claims ready executions from SQL and supervises at most the configured number of children.</summary>
public sealed class ExecutionDispatchLoop(
    IExecutionQueue queue,
    IAttemptStore attempts,
    IAttemptRunner runner,
    TimeProvider clock,
    int maxChildren)
{
    private readonly string workerBootId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> active = new();
    private int reservedChildren;
    private int stopped;

    public async ValueTask<int> RunOnceAsync(CancellationToken ct)
    {
        if (maxChildren is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxChildren));
        await dispatchGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref stopped) != 0) return 0;
            await attempts.ReviewExpiredAsync(100, ct);
            var available = maxChildren - Volatile.Read(ref reservedChildren);
            if (available <= 0) return 0;

            var ready = await queue.ReadReadyAsync(available, ct);
            var started = 0;
            foreach (var executionId in ready.Take(available))
            {
                if (Volatile.Read(ref stopped) != 0) break;
                Interlocked.Increment(ref reservedChildren);
                var taskId = Guid.NewGuid();
                var kickoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () =>
                {
                    await kickoff.Task;
                    await DispatchAsync(executionId, taskId, ct);
                });
                active[taskId] = task;
                kickoff.SetResult();
                started++;
            }
            return started;
        }
        finally { dispatchGate.Release(); }
    }

    /// <summary>Stops admission, cancels active runs, and waits for child containment to finish.</summary>
    public async ValueTask DrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Interlocked.Exchange(ref stopped, 1);
        stopping.Cancel();
        await dispatchGate.WaitAsync(ct);
        try
        {
            if (runner is OwnershipMonitor monitor)
                await monitor.DrainAsync(timeout, ct);
            var tasks = active.Values.ToArray();
            if (tasks.Length > 0) await Task.WhenAll(tasks).WaitAsync(timeout, ct);
        }
        finally { dispatchGate.Release(); }
    }

    private async Task DispatchAsync(Guid executionId, Guid taskId, CancellationToken requestToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, stopping.Token);
        try
        {
            var admission = await attempts.BeginAsync(executionId, workerBootId, linked.Token);
            if (admission.Decision != "Started" || admission.Lease is not { } lease) return;

            ExecutionDescriptor descriptor;
            var authorizationStarted = clock.GetTimestamp();
            try { descriptor = await attempts.GetDescriptorAsync(lease, linked.Token); }
            catch
            {
                using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await attempts.CompleteAsync(lease, new(null, "ProcessFailed", true, false), persist.Token);
                return;
            }

            await runner.RunAsync(lease, descriptor, authorizationStarted, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch { /* A later poll retries persistence and admission failures. */ }
        finally
        {
            active.TryRemove(taskId, out _);
            Interlocked.Decrement(ref reservedChildren);
        }
    }
}
