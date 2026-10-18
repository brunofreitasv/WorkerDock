namespace WorkerDock.Templates.SqlServer;

internal sealed class SqlDeadlineCancellation : IDisposable
{
    private static readonly TimeSpan MaximumTimerDelay=TimeSpan.FromDays(24);
    private readonly CancellationTokenSource cancellation;
    private readonly DateTimeOffset deadline;
    private readonly TimeProvider clock;
    private readonly object gate=new();
    private readonly ITimer timer;
    private bool disposed;

    private SqlDeadlineCancellation(CancellationTokenSource cancellation,DateTimeOffset deadline,TimeProvider clock)
    {
        this.cancellation=cancellation;this.deadline=deadline;this.clock=clock;
        timer=clock.CreateTimer(static state=>((SqlDeadlineCancellation)state!).OnTimer(),this,Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan);
    }

    internal static SqlDeadlineCancellation Schedule(CancellationTokenSource cancellation,DateTimeOffset deadline,TimeProvider clock)
    {
        var schedule=new SqlDeadlineCancellation(cancellation,deadline,clock);
        schedule.OnTimer();
        return schedule;
    }

    private void OnTimer()
    {
        var cancel=false;
        lock(gate)
        {
            if(disposed)return;
            var remaining=deadline-clock.GetUtcNow();
            if(remaining<=TimeSpan.Zero)cancel=true;
            else timer.Change(remaining>MaximumTimerDelay?MaximumTimerDelay:remaining,Timeout.InfiniteTimeSpan);
        }
        if(cancel)
        {
            try{cancellation.Cancel();}
            catch(ObjectDisposedException){ }
        }
    }

    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;
            disposed=true;
        }
        timer.Dispose();
    }
}
