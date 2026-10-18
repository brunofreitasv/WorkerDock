namespace WorkerDock.Domain.Executions;

public enum ExecutionState
{
    Queued, Running, RetryWaiting, NeedsReview, Succeeded, Failed, Stopped, TimedOut, Skipped
}

public enum OutcomeCertainty { Known, Unknown }
