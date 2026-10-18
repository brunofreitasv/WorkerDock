using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Application.Jobs;
using WorkerDock.Contracts.Execution;
using WorkerDock.Domain.Jobs;

namespace WorkerDock.Infrastructure.Executions;

public sealed record AttemptSnapshot(string State, int Ordinal, bool PermitRetained, bool StopRequested,
    string? AttemptState, bool EffectsMayHaveOccurred);
public sealed record MemoryExecutionAudit(string Decision, string Reason, ActorContext Actor, DateTimeOffset OccurredUtc);
public sealed class InMemoryAttemptStore : IAttemptStore, IExecutionControls, IAttemptSignals
{
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly AttemptLifecycle data;
    private readonly List<DispatchIntent> retries = [];
    private readonly List<MemoryExecutionAudit> audits = [];
    public InMemoryAttemptStore(TimeProvider clock, ExecutionDescriptor descriptor, ExecutionPolicy policy)
    {
        this.clock = clock;
        Intent = new(Guid.NewGuid(), descriptor.ExecutionId, 1, descriptor.TemplateId, clock.GetUtcNow(), policy.Priority, 1, "");
        Intent = Intent with { PayloadDigest = Intent.ComputeDigest() };
        data = new() { Descriptor = descriptor, Policy = policy, Intent = Intent, JobId = Guid.NewGuid() };
    }
    public DispatchIntent Intent { get; }
    public AttemptSnapshot Snapshot { get { lock (gate) return new(data.State, data.Ordinal, data.PermitRetained, data.StopRequested, data.AttemptState, data.Uncertain); } }
    public IReadOnlyList<DispatchIntent> Retries { get { lock (gate) return retries.ToArray(); } }
    public IReadOnlyList<MemoryExecutionAudit> Audits { get { lock (gate) return audits.ToArray(); } }
    public AttemptLease? CurrentLease { get { lock (gate) return data.Lease; } }
    public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) => Run(() => data.Begin(intent, workerBootId, clock.GetUtcNow()), ct);
    public ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct) => Run(() => data.GetDescriptor(lease, clock.GetUtcNow()), ct);
    public ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct) => Run(() => data.Renew(lease, clock.GetUtcNow()), ct);
    public async ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct) =>
        await Run(() => { data.Complete(lease, completion, clock.GetUtcNow()); return true; }, ct);
    public ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return Run(() => data.Review(clock.GetUtcNow()) ? 1 : 0, ct);
    }
    public ValueTask<CommandReceipt> StopAsync(Guid executionId, bool force, string reason, ActorContext actor, CancellationToken ct) => Control(executionId, "Stop", reason, actor, () => data.Stop(force), ct);
    public ValueTask<CommandReceipt> ResolveAsync(Guid executionId, string decision, string reason, ActorContext actor, CancellationToken ct) => Control(executionId, decision, reason, actor, () => data.Resolve(decision, clock.GetUtcNow()), ct);
    private ValueTask<CommandReceipt> Control(Guid id, string kind, string reason, ActorContext actor, Action action, CancellationToken ct)
    {
        ValidateControl(id, reason, actor);
        return Run(() =>
        {
            if (id != data.Descriptor.ExecutionId) throw new WorkerDockException("ExecutionNotFound");
            action(); audits.Add(new(kind, reason, actor, clock.GetUtcNow()));
            return new CommandReceipt(Guid.NewGuid(), id, data.State == "Running" ? "Pending" : "Applied");
        }, ct);
    }
    internal static void ValidateControl(Guid id, string reason, ActorContext actor)
    {
        JobCommandValidation.ValidateActor(actor);
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new WorkerDockException("ValidationFailed", "reason");
    }
    public ValueTask<AttemptSignal> ReadSignalAsync(AttemptLease lease, CancellationToken ct) => Run(() => new AttemptSignal(data.Owned(lease, clock.GetUtcNow()), data.StopRequested, data.Force), ct);
    private ValueTask<T> Run<T>(Func<T> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var oldIntent = data.Intent;
            var result = action();
            if (oldIntent != data.Intent && data.NewDispatch is { } intent) retries.Add(intent);
            return ValueTask.FromResult(result);
        }
    }
}
