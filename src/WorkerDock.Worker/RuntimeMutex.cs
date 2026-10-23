using WorkerDock.Application.Common;

namespace WorkerDock.Worker;

/// <summary>One tenant runtime per machine. The owning thread also releases the mutex.</summary>
public sealed class RuntimeMutex : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly Thread owner;
    private int disposed;

    private RuntimeMutex(Guid instanceId)
    {
        var acquired = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner = new Thread(() => Own(instanceId, acquired)) { IsBackground = true, Name = "WorkerDock runtime mutex" };
        owner.Start();
        if (acquired.Task.GetAwaiter().GetResult() is { } error)
        {
            release.Set();
            owner.Join();
            release.Dispose();
            throw error;
        }
    }

    public static IDisposable Acquire(Guid instanceId)
    {
        if (instanceId == Guid.Empty) throw new WorkerDockException("WorkerConfigurationUnavailable");
        return new RuntimeMutex(instanceId);
    }

    private void Own(Guid instanceId, TaskCompletionSource<Exception?> acquired)
    {
        Mutex? mutex = null;
        var owns = false;
        try
        {
            mutex = new Mutex(false, $@"Global\WorkerDock.{instanceId:N}");
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            acquired.TrySetResult(owns ? null : new WorkerDockException("RuntimeAlreadyRunning"));
            if (owns) release.Wait();
        }
        catch (Exception error)
        {
            acquired.TrySetResult(error is WorkerDockException ? error : new WorkerDockException("RuntimeMutexUnavailable"));
        }
        finally
        {
            if (owns) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set();
        owner.Join();
        release.Dispose();
    }
}
