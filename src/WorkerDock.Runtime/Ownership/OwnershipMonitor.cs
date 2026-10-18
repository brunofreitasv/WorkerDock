using System.Collections.Concurrent;
using WorkerDock.Application.Execution;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Runtime.Ownership;

public sealed class OwnershipMonitor(IAttemptStore store, IAttemptSignals signals, IExecutionSupervisor supervisor, TimeProvider clock) : IAttemptRunner
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> active = new();
    private int stopping, forceShutdown;

    public async ValueTask RunAsync(AttemptLease lease, ExecutionDescriptor descriptor, long authorizationStarted, CancellationToken ct)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!active.TryAdd(lease.AttemptId, finished)) throw new InvalidOperationException("AttemptAlreadySupervised");
        IExecutionChild? child = null;
        TemplateResult? result = null;
        var outcome = "ProcessFailed"; var exitConfirmed = false; var effects = true;
        var acknowledgedAt = authorizationStarted;
        var finiteBudget = lease.DeadlineUtc is { } deadline ? deadline - (lease.ExpiresUtc - OwnershipDefaults.LeaseDuration) : (TimeSpan?)null;
        TimeSpan Remaining() => OwnershipDefaults.LeaseDuration - OwnershipDefaults.SafetyMargin - clock.GetElapsedTime(acknowledgedAt);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var startupBudget = Remaining();
            if (finiteBudget is { } finite) startupBudget = Min(startupBudget, finite - clock.GetElapsedTime(authorizationStarted));
            if (startupBudget <= TimeSpan.Zero || Volatile.Read(ref stopping) != 0) throw new OperationCanceledException();
            startup.CancelAfter(startupBudget);
            child = await supervisor.StartAsync(lease.AttemptId, lease.Generation, descriptor, startup.Token);
            var completion = child.WaitAsync(default).AsTask();
            var lastRenew = clock.GetTimestamp(); var lastPoll = lastRenew;
            long? cancellationAt = null;
            while (true)
            {
                var now = clock.GetTimestamp();
                // A completion exposed between polls still has to satisfy the admitted attempt's deadline.
                if (finiteBudget is { } budget && clock.GetElapsedTime(authorizationStarted, now) >= budget && cancellationAt is null)
                { outcome = "TimedOut"; cancellationAt = now; }
                if (completion.IsCompleted)
                {
                    result = await completion; exitConfirmed = true; effects = result.EffectsMayHaveOccurred;
                    if (cancellationAt is null)
                        outcome = finiteBudget is { } completionBudget && clock.GetElapsedTime(authorizationStarted) >= completionBudget
                            ? "TimedOut" : "Completed";
                    break;
                }
                var force = Volatile.Read(ref forceShutdown) != 0;
                // A late acknowledgement cannot revive a child after the conservative local safety boundary.
                force |= Remaining() <= TimeSpan.FromSeconds(10);
                if (ct.IsCancellationRequested || Volatile.Read(ref stopping) != 0 || Remaining() <= OwnershipDefaults.CancellationGrace + TimeSpan.FromSeconds(5))
                    cancellationAt ??= now;
                if (!force && clock.GetElapsedTime(lastPoll) >= TimeSpan.FromSeconds(1))
                {
                    lastPoll = now;
                    try
                    {
                        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        var signal = await signals.ReadSignalAsync(lease, request.Token).AsTask().WaitAsync(request.Token);
                        if (!signal.Owned) { cancellationAt ??= now; force = true; }
                        if (signal.StopRequested) { outcome = "Stopped"; cancellationAt ??= now; force |= signal.Force; }
                    }
                    catch { }
                }
                if (!force && Remaining() > TimeSpan.FromSeconds(10) && clock.GetElapsedTime(lastRenew) >= OwnershipDefaults.RenewalInterval)
                {
                    lastRenew = clock.GetTimestamp();
                    try
                    {
                        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var renewed = await store.RenewAsync(lease, request.Token).AsTask().WaitAsync(request.Token);
                        // Keep the old acknowledged reference until this await's elapsed time has been checked.
                        if (renewed is null || Remaining() <= TimeSpan.FromSeconds(10))
                        { cancellationAt ??= clock.GetTimestamp(); force = true; }
                        else { lease = renewed; acknowledgedAt = lastRenew; }
                    }
                    catch { /* Never extend the monotonic lease without a database acknowledgement. */ }
                }
                if (cancellationAt is { } cancellation)
                {
                    if (force || clock.GetElapsedTime(cancellation) >= OwnershipDefaults.CancellationGrace || Remaining() <= TimeSpan.FromSeconds(5))
                    {
                        using var kill = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await child.TerminateAsync(kill.Token); exitConfirmed = true; break;
                    }
                    if (cancellation == now)
                    {
                        try { using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await child.CancelAsync(cancel.Token).AsTask().WaitAsync(cancel.Token); }
                        catch { }
                    }
                }
                await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMilliseconds(200)));
            }
        }
        catch
        {
            if (child is not null)
            {
                try { using var kill = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await child.TerminateAsync(kill.Token); exitConfirmed = true; }
                catch { exitConfirmed = false; }
            }
        }
        finally
        {
            try
            {
                if (child is not null)
                {
                    try { await child.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6)); }
                    catch { exitConfirmed = false; }
                }
                using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await store.CompleteAsync(lease, new(result, outcome, exitConfirmed, effects), persist.Token);
            }
            finally { active.TryRemove(lease.AttemptId, out _); finished.TrySetResult(); }
        }
    }

    public async ValueTask DrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Interlocked.Exchange(ref stopping, 1);
        var tasks = active.Values.Select(x => x.Task).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(timeout, ct); }
        catch (Exception e) when (e is TimeoutException || e is OperationCanceledException && ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref forceShutdown, 1);
            // Cancellation stops the caller's drain wait; it cannot skip the owner's bounded containment cleanup.
            await Task.WhenAll(active.Values.Select(x => x.Task)).WaitAsync(TimeSpan.FromSeconds(15));
            ct.ThrowIfCancellationRequested();
        }
    }
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
