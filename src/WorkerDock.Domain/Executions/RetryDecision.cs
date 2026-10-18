using WorkerDock.Domain.Jobs;

namespace WorkerDock.Domain.Executions;

public sealed record RetryDecision(bool Retry, TimeSpan Delay)
{
    public static RetryDecision Decide(ExecutionPolicy policy, int ordinal, string? outcome, string? code,
        bool uncertain, bool stopped, TimeSpan? retryAfter, double random)
    {
        if (stopped || ordinal >= policy.MaxAttempts ||
            (uncertain ? !policy.RetryUncertain : outcome != "TransientFailure" || !policy.RetryableCodes.Contains(code ?? "")))
            return new(false, TimeSpan.Zero);
        var maximum = policy.RetryMaxDelay.TotalMilliseconds;
        var exponential = Math.Min(maximum, policy.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(ordinal - 1, 30)));
        var jitter = exponential * (1 - policy.JitterRatio + 2 * policy.JitterRatio * Math.Clamp(random, 0, 1));
        var hint = retryAfter is { } value && value >= TimeSpan.Zero ? Math.Min(maximum, value.TotalMilliseconds) : 0;
        return new(true, TimeSpan.FromMilliseconds(Math.Clamp(Math.Max(jitter, hint), 0, maximum)));
    }
}
