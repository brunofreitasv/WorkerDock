namespace WorkerDock.Domain.Jobs;

public sealed record ExecutionPolicy(TimeSpan? Timeout, int MaxAttempts, TimeSpan RetryBaseDelay,
    TimeSpan RetryMaxDelay, double JitterRatio, IReadOnlyList<string> RetryableCodes,
    bool RetryUncertain, string Overlap, int BacklogLimit, int Priority);
