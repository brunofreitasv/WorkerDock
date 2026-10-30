using WorkerDock.Application.Common;

namespace WorkerDock.Worker;

public sealed record RuntimeLoop(TimeSpan Interval, Func<CancellationToken, ValueTask> Work);

/// <summary>Independent bounded polls that admit work only while SQL heartbeat is available.</summary>
public sealed class RuntimeLoops(Func<CancellationToken, ValueTask> heartbeat)
{
    private int ready;
    public bool Ready => Volatile.Read(ref ready) != 0;

    public async ValueTask PulseAsync(CancellationToken ct)
    {
        try
        {
            await heartbeat(ct);
            Volatile.Write(ref ready, 1);
        }
        catch
        {
            Volatile.Write(ref ready, 0);
            throw;
        }
    }

    public async ValueTask RunWorkOnceAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
    {
        if (!Ready) return;
        try { await work(ct); }
        catch
        {
            Volatile.Write(ref ready, 0);
            throw;
        }
    }

    public async Task RunAsync(IReadOnlyList<RuntimeLoop> work, CancellationToken ct)
    {
        if (work.Any(loop => loop.Interval <= TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(work));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task>(work.Count + 1)
        {
            RepeatAsync(token => PulseAsync(token), TimeSpan.FromSeconds(5), stop, ct)
        };
        tasks.AddRange(work.Select(loop => RepeatAsync(
            token => RunWorkOnceAsync(loop.Work, token), loop.Interval, stop, ct)));
        try { await Task.WhenAll(tasks); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private static async Task RepeatAsync(Func<CancellationToken, ValueTask> action, TimeSpan interval,
        CancellationTokenSource stop, CancellationToken external)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await action(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (WorkerDockException error) when (error.Code is "TenantInstanceMismatch" or "RuntimeStatusUnavailable" or "WorkerConfigurationUnavailable")
            {
                stop.Cancel();
                throw;
            }
            catch { /* A failed SQL poll closes admission until the next successful heartbeat. */ }
            try { await Task.Delay(interval, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
        external.ThrowIfCancellationRequested();
    }
}
