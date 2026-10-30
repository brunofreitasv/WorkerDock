using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Application.Jobs;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Contracts.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Values;
using System.Text.Json;

namespace WorkerDock.Infrastructure.Executions;

public sealed record AttemptSnapshot(string State, int Ordinal, bool HoldsJobSlot, bool StopRequested,
    string? AttemptState, bool EffectsMayHaveOccurred);
public sealed record MemoryExecutionAudit(string Decision, string Reason, ActorContext Actor, DateTimeOffset OccurredUtc);
public sealed class InMemoryAttemptStore : IAttemptStore, IExecutionControls, IAttemptSignals
{
    private readonly TimeProvider clock;
    private readonly Action<AttemptSnapshot>? committed;
    private readonly Func<CancellationToken, ValueTask<JsonElement>>? integrationSnapshot;
    private readonly ExecutionValuePreparation values;
    private readonly SemaphoreSlim descriptorRefresh = new(1, 1);
    private int snapshotOrdinal;
    private readonly object gate = new();
    private readonly AttemptLifecycle data;
    private readonly List<DispatchIntent> retries = [];
    private readonly List<MemoryExecutionAudit> audits = [];
    private readonly Dictionary<Guid, FailureDiagnostic> failureDiagnostics = [];
    public IReadOnlyDictionary<Guid, FailureDiagnostic> FailureDiagnostics { get { lock (gate) return new Dictionary<Guid, FailureDiagnostic>(failureDiagnostics); } }
    public InMemoryAttemptStore(TimeProvider clock, ExecutionDescriptor descriptor, ExecutionPolicy policy, Action<AttemptSnapshot>? committed = null,
        Func<CancellationToken, ValueTask<JsonElement>>? integrationSnapshot = null,
        ExecutionValuePreparation? values = null, DynamicValuePlan? dynamicValues = null)
    {
        this.clock = clock; this.committed = committed; this.integrationSnapshot = integrationSnapshot;
        this.values = values ?? new ExecutionValuePreparation(new DynamicValueResolver());
        Intent = new(Guid.NewGuid(), descriptor.ExecutionId, 1, descriptor.TemplateId, clock.GetUtcNow(), policy.Priority, 1, "");
        Intent = Intent with { PayloadDigest = Intent.ComputeDigest() };
        data = new() { Descriptor = descriptor, OriginalConfiguration = descriptor.Configuration.Clone(), DynamicValues = dynamicValues is null ? null : DynamicValueJson.Freeze(dynamicValues), Policy = policy, Intent = Intent, JobId = Guid.NewGuid() };
    }
    public DispatchIntent Intent { get; }
    public ExecutionDescriptor AcceptedDescriptor => data.Descriptor;
    public AttemptSnapshot Snapshot { get { lock (gate) return new(data.State, data.Ordinal, data.HoldsJobSlot, data.StopRequested, data.AttemptState, data.Uncertain); } }
    public IReadOnlyList<DispatchIntent> Retries { get { lock (gate) return retries.ToArray(); } }
    public IReadOnlyList<MemoryExecutionAudit> Audits { get { lock (gate) return audits.ToArray(); } }
    public AttemptLease? CurrentLease { get { lock (gate) return data.Lease; } }
    public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) =>
        Run(() => data.Begin(intent, workerBootId, clock.GetUtcNow()), ct);
    public ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct) =>
        Run(() => BeginDirect(executionId, workerBootId, clock.GetUtcNow()), ct);
    private AttemptStart BeginDirect(Guid executionId, string workerBootId, DateTimeOffset now)
    {
        if (executionId != data.Descriptor.ExecutionId) throw new WorkerDockException("ExecutionNotFound");
        return data.Begin(data.Intent, workerBootId, now);
    }
    public async ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct)
    {
        await descriptorRefresh.WaitAsync(ct);
        try
        {
            var cached = await Run(() =>
            {
                data.GetDescriptor(lease, clock.GetUtcNow());
                data.Prepare(values);
                return (snapshotOrdinal == lease.Ordinal, data.GetDescriptor(lease, clock.GetUtcNow()));
            }, ct);
            if (cached.Item1 || integrationSnapshot is null || cached.Item2.ValueResolutionError is not null)
                return await Run(() => Bound(data.GetDescriptor(lease, clock.GetUtcNow())), ct);
            var snapshot = (await integrationSnapshot(ct)).Clone();
            return await Run(() =>
            {
                if (snapshotOrdinal != lease.Ordinal)
                {
                    data.Descriptor = data.Descriptor with { IntegrationConfiguration = snapshot };
                    snapshotOrdinal = lease.Ordinal;
                }
                return Bound(data.GetDescriptor(lease, clock.GetUtcNow()));
            }, ct);
        }
        finally { descriptorRefresh.Release(); }
    }
    private ExecutionDescriptor Bound(ExecutionDescriptor descriptor)
    {
        if (descriptor.ValueSnapshot is null || ExecutionPayloadLimits.FitsPreparedDescriptor(descriptor)) return descriptor;
        data.Descriptor = data.Descriptor with { ValueSnapshot = null, ValueResolutionError = "DynamicSnapshotTooLarge",
            Configuration = data.OriginalConfiguration };
        return descriptor with { ValueSnapshot = null, ValueResolutionError = "DynamicSnapshotTooLarge",
            Configuration = data.OriginalConfiguration };
    }
    public ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct) => Run(() => data.Renew(lease, clock.GetUtcNow()), ct);
    public async ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct) =>
        await Run(() =>
        {
            var now = clock.GetUtcNow();
            var owned = data.Owned(lease, now);
            data.Complete(lease, completion, now);
            if (owned && data.FailureDiagnostic is { } diagnostic) failureDiagnostics.TryAdd(lease.AttemptId, diagnostic);
            return true;
        }, ct);
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
            return new CommandReceipt(Guid.NewGuid(), id, data.State == "Running" ? "Pending" : data.State == "NeedsReview" ? "Failed" : "Applied");
        }, ct);
    }
    private void Notify() => committed?.Invoke(new(data.State, data.Ordinal, data.HoldsJobSlot, data.StopRequested, data.AttemptState, data.Uncertain));
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
            Notify();
            return ValueTask.FromResult(result);
        }
    }
}
