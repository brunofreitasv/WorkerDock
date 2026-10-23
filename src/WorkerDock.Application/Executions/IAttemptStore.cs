using WorkerDock.Application.Dispatch;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Application.Executions;

public sealed record AttemptLease(Guid AttemptId, Guid ExecutionId, int Ordinal, long Generation,
    string WorkerBootId, DateTimeOffset ExpiresUtc, DateTimeOffset? DeadlineUtc);
public sealed record AttemptStart(string Decision, AttemptLease? Lease);
public sealed record AttemptCompletion(TemplateResult? HandlerResult, string LocalOutcome,
    bool LocalExitConfirmed, bool EffectsMayHaveOccurred);
public sealed record AttemptSignal(bool Owned, bool StopRequested, bool Force);

public interface IAttemptStore
{
    ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct);
    ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct);
    ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct);
    ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct);
    ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct);
    ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct);
}

public interface IAttemptSignals
{
    ValueTask<AttemptSignal> ReadSignalAsync(AttemptLease lease, CancellationToken ct);
}

public interface IAttemptRunner
{
    ValueTask RunAsync(AttemptLease lease, ExecutionDescriptor descriptor, long authorizationStarted,
        CancellationToken ct);
}

public sealed record WorkerIdentity(string BootId);

public static class OwnershipDefaults
{
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(5);
}
