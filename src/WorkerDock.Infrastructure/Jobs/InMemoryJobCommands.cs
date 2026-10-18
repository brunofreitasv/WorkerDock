using WorkerDock.Application.Connections;
using System.Collections.Immutable;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Infrastructure.Executions;
using System.Text.Json;
using WorkerDock.Domain.Jobs;

namespace WorkerDock.Infrastructure.Jobs;

public sealed record MemoryExecution(Guid ExecutionId, Guid JobId, long JobRevision, Guid? ProfileId,
    string State, string IdempotencyKey);

public sealed class InMemoryJobCommands : IJobCommands, IExecutionControls, IProjectionStore
{
    private readonly TimeProvider clock;
    private readonly IConnectionProfiles? profiles;
    private readonly Func<string, CancellationToken, ValueTask>? beforeCommit;
    private readonly SemaphoreSlim gate = new(1, 1);
    private State state = new([], [], [], [], [], [], []);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, InMemoryAttemptStore> attemptStores = new();

    public InMemoryJobCommands(TimeProvider clock, IEnumerable<JobRevision>? enabledSeeds = null,
        Func<string, CancellationToken, ValueTask>? beforeCommit = null,
        IEnumerable<JobRevision>? archivedSeeds = null, IConnectionProfiles? profiles = null)
    {
        this.clock = clock; this.profiles=profiles;
        this.beforeCommit = beforeCommit;
        foreach (var seed in enabledSeeds ?? []) Seed(seed, false);
        foreach (var seed in archivedSeeds ?? []) Seed(seed, true);
    }

    private void Seed(JobRevision seed, bool archived)
    {
        JobCommandValidation.ValidateRevision(seed);
        var revision = JobCommandValidation.Freeze(seed with { Revision = 1 });
        var editor = new JobEditorDto(revision, BitConverter.GetBytes(1L), true, archived, 1);
        state = state with
        {
            Jobs = state.Jobs.Add(seed.JobId, editor),
            Revisions = state.Revisions.Add((seed.JobId, 1), revision)
        };
    }

    public int AuditCount => Volatile.Read(ref state).Audits.Length;
    public ImmutableArray<MemoryJobAudit> AuditEvents => Volatile.Read(ref state).Audits;
    public int OutboxCount => Volatile.Read(ref state).Outbox.Length;
    public int ExecutionCount => Volatile.Read(ref state).Executions.Count;
    public MemoryExecution? GetExecution(Guid id)
    {
        var execution = Volatile.Read(ref state).Executions.GetValueOrDefault(id);
        return execution is not null && attemptStores.TryGetValue(id, out var attempts)
            ? execution with { State = attempts.Snapshot.State } : execution;
    }
    public InMemoryAttemptStore? GetAttemptStore(Guid id) => attemptStores.GetValueOrDefault(id);
    public JobRevision? GetRevision(Guid jobId, long revision) => Volatile.Read(ref state).Revisions.GetValueOrDefault((jobId, revision));

    public async ValueTask<CommandReceipt> SaveAsync(JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        JobCommandValidation.ValidateRevision(proposed);
        if (expectedVersion is not null && expectedVersion.Length != 8)
            throw new WorkerDockException("ValidationFailed", "version");
        await gate.WaitAsync(ct);
        try
        {
            var before = state;
            var exists = before.Jobs.TryGetValue(proposed.JobId, out var editor);
            if (expectedVersion is null && exists) throw new WorkerDockException("JobExists");
            if (expectedVersion is not null && !exists) throw new WorkerDockException("JobNotFound");
            if (exists && !editor!.Version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
            var number = exists ? editor!.Revision.Revision + 1 : 1;
            var revision = JobCommandValidation.Freeze(proposed with { Revision = number });
            var next = new JobEditorDto(revision, BitConverter.GetBytes(number), exists && editor!.Enabled,
                exists && editor!.Archived, exists ? editor!.ConcurrencyLimit : 1);
            var receipt = new CommandReceipt(Guid.NewGuid(), proposed.JobId, "Applied");
            await CheckPhaseAsync("Audit", ct);
            var audit = new MemoryJobAudit(Guid.NewGuid(), "JobRevisionSaved", proposed.JobId,
                exists ? editor!.Revision.Revision : null, number, "Applied", actor.Subject,
                actor.AuthenticationMode, actor.CorrelationId, clock.GetUtcNow());
            ImmutableInterlocked.Update(ref state, current => before with
            {
                Jobs = before.Jobs.SetItem(proposed.JobId, next),
                Revisions = before.Revisions.Add((proposed.JobId, number), revision),
                Commands = current.Commands.Add(receipt.CommandId, receipt),
                CommandKinds = current.CommandKinds.Add(receipt.CommandId, "JobSave"),
                Audits = current.Audits.Add(audit)
            });
            return receipt;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateRequestKey(jobId, requestKey, actor);
        await gate.WaitAsync(ct);
        try
        {
            var before = state;
            var now = clock.GetUtcNow();
            var scope = (jobId, actor.AuthenticationMode, actor.Subject, requestKey);
            if (before.Dedup.TryGetValue(scope, out var old) && now < old.ExpiresUtc)
                return before.Commands.GetValueOrDefault(old.Receipt.CommandId, old.Receipt);
            if (!before.Jobs.TryGetValue(jobId, out var editor)) throw new WorkerDockException("JobNotFound");
            if (editor.Archived) throw new WorkerDockException("JobArchived");
            if (!editor.Enabled) throw new WorkerDockException("JobDisabled");
            WorkerDock.Contracts.Connections.ConnectionProfileRevision? pinnedProfile=null;
            if(editor.Revision.ProfileId is {} profileId)
                pinnedProfile=profiles is not null ? (await profiles.GetCurrentAsync(profileId,ct)).Current : throw new WorkerDockException("ProfileUnavailable");
            var executionId = Guid.NewGuid();
            var receipt = new CommandReceipt(Guid.NewGuid(), executionId, "Pending");
            var execution = new MemoryExecution(executionId, jobId, editor.Revision.Revision,
                editor.Revision.ProfileId, "Queued", executionId.ToString("N"));
            await CheckPhaseAsync("Audit", ct);
            await CheckPhaseAsync("Outbox", ct);
            var digest = ProjectionEnvelope.ExecutionDigest(executionId, editor.Revision.Revision);
            ImmutableInterlocked.Update(ref state, current => before with
            {
                Executions = before.Executions.Add(executionId, execution),
                Commands = current.Commands.Add(receipt.CommandId, receipt),
                CommandKinds = current.CommandKinds.Add(receipt.CommandId, "ManualRun"),
                Dedup = before.Dedup.SetItem(scope, new(receipt, now.AddHours(24))),
                Audits = current.Audits.Add(new(Guid.NewGuid(), "ManualRunAccepted", executionId,
                    editor.Revision.Revision, editor.Revision.Revision, "Accepted", actor.Subject,
                    actor.AuthenticationMode, actor.CorrelationId, now)),
                Outbox = before.Outbox.Add(new(Guid.NewGuid(), "ExecutionDispatch", executionId,
                    editor.Revision.Revision, digest, now))
            });
            attemptStores[executionId] = new InMemoryAttemptStore(clock,
                new ExecutionDescriptor(executionId, 1, editor.Revision.TemplateId, editor.Revision.TemplateVersion,
                    execution.IdempotencyKey, editor.Revision.Configuration, pinnedProfile?.ProfileRevisionId, pinnedProfile is null ? null : JsonSerializer.SerializeToElement(pinnedProfile), JsonSerializer.SerializeToElement(new { }), null), editor.Revision.Policy, committed: snapshot => SettleCommands(executionId, snapshot));
            return receipt;
        }
        finally { gate.Release(); }
    }

    private void SettleCommands(Guid executionId, AttemptSnapshot snapshot) =>
        ImmutableInterlocked.Update(ref state, before => Settle(before, executionId, snapshot));

    private State Settle(State before, Guid executionId, AttemptSnapshot snapshot, bool rejected = false)
    {
        var commands = before.Commands; var audits = before.Audits;
        var published = before.Outbox.Any(row => row.EntityId == executionId && row.State == "Published");
        foreach (var receipt in before.Commands.Values.Where(row => row.TargetId == executionId && row.State == "Pending"))
        {
            var manual = before.CommandKinds.GetValueOrDefault(receipt.CommandId) == "ManualRun";
            var outcome = manual ? snapshot.Ordinal > 0 || published ? "Applied" : rejected || snapshot.State is "Stopped" or "Skipped" or "Failed" ? "Failed" : "Pending"
                : snapshot.State == "Running" ? "Pending" : snapshot.State == "NeedsReview" ? "Failed" : "Applied";
            if (outcome == "Pending") continue;
            commands = commands.SetItem(receipt.CommandId, receipt with { State = outcome });
            audits = audits.Add(new(Guid.NewGuid(), "CommandSettled", executionId, null, 1, outcome,
                "workerdock", "System", receipt.CommandId, clock.GetUtcNow()));
        }
        return before with { Commands = commands, Audits = audits };
    }

    public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPendingAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ProjectionEnvelope> result = Volatile.Read(ref state).Outbox
            .Where(row => row.State is "Pending" or "Failed" && (row.RetryAtUtc is null || row.RetryAtUtc <= clock.GetUtcNow()))
            .OrderBy(row => row.AvailableUtc).Take(limit).Select(row => new ProjectionEnvelope(row.EventId, row.Kind, 1,
                row.EntityId, row.Revision, row.PayloadDigest, row.AvailableUtc,
                JsonSerializer.SerializeToElement(new { executionId = row.EntityId, jobRevision = row.Revision }))).ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask AcknowledgeAsync(Guid intentId, string payloadDigest, CancellationToken ct) =>
        UpdateProjectionAsync(intentId, payloadDigest, "Published", null, false, ct);
    public ValueTask RecordFailureAsync(Guid intentId, string safeCode, DateTimeOffset retryAtUtc, CancellationToken ct)
    {
        if (safeCode is not ("UnsupportedProjection" or "ProjectionIntegrityError" or "ProjectionTriggerError" or
            "ProjectionPublicationRace" or "ProjectionHeld" or "QuartzStoreUnavailable" or "ProjectionStoreUnavailable"))
            throw new ArgumentException("Unknown projection failure code.", nameof(safeCode));
        return UpdateProjectionAsync(intentId, null, safeCode == "ProjectionHeld" ? "Held" : "Failed", retryAtUtc,
            safeCode is "UnsupportedProjection" or "ProjectionIntegrityError", ct);
    }
    private async ValueTask UpdateProjectionAsync(Guid intentId, string? digest, string outcome, DateTimeOffset? retry,
        bool rejected, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var row = state.Outbox.SingleOrDefault(row => row.EventId == intentId);
            if (row is null || digest is not null && row.PayloadDigest != digest) throw new WorkerDockException("ProjectionIntegrityError");
            await CheckPhaseAsync("Audit", ct); await CheckPhaseAsync("Outbox", ct);
            var snapshot = attemptStores[row.EntityId].Snapshot;
            ImmutableInterlocked.Update(ref state, before => Settle(before with
            {
                Outbox = before.Outbox.Replace(row, row with { State = row.State == "Published" ? "Published" : outcome, RetryAtUtc = retry })
            }, row.EntityId, snapshot, rejected));
        }
        finally { gate.Release(); }
    }

    public ValueTask<CommandReceipt> GetCommandAsync(Guid commandId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Volatile.Read(ref state).Commands.TryGetValue(commandId, out var receipt)
            ? ValueTask.FromResult(receipt)
            : ValueTask.FromException<CommandReceipt>(new WorkerDockException("CommandNotFound"));
    }

    public ValueTask<CommandReceipt> StopAsync(Guid executionId, bool force, string reason, ActorContext actor, CancellationToken ct) =>
        ControlAsync(executionId, "Stop", actor, store => store.StopAsync(executionId, force, reason, actor, ct), ct);
    public ValueTask<CommandReceipt> ResolveAsync(Guid executionId, string decision, string reason, ActorContext actor, CancellationToken ct) =>
        ControlAsync(executionId, decision, actor, store => store.ResolveAsync(executionId, decision, reason, actor, ct), ct);
    private async ValueTask<CommandReceipt> ControlAsync(Guid id, string kind, ActorContext actor,
        Func<InMemoryAttemptStore, ValueTask<CommandReceipt>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!attemptStores.TryGetValue(id, out var store)) throw new WorkerDockException("ExecutionNotFound");
            await CheckPhaseAsync("Audit", ct);
            var receipt = await action(store);
            ImmutableInterlocked.Update(ref state, current => current with
            {
                Commands = current.Commands.Add(receipt.CommandId, receipt),
                CommandKinds = current.CommandKinds.Add(receipt.CommandId, kind),
                Audits = current.Audits.Add(new(Guid.NewGuid(), kind, id, null, 1, receipt.State, actor.Subject,
                    actor.AuthenticationMode, actor.CorrelationId, clock.GetUtcNow()))
            });
            SettleCommands(id, store.Snapshot);
            return receipt;
        }
        finally { gate.Release(); }
    }

    public ValueTask<JobEditorDto> GetEditableAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Volatile.Read(ref state).Jobs.TryGetValue(jobId, out var editor))
            return ValueTask.FromException<JobEditorDto>(new WorkerDockException("JobNotFound"));
        return ValueTask.FromResult(editor with
        {
            Revision = JobCommandValidation.Freeze(editor.Revision),
            Version = (byte[])editor.Version.Clone()
        });
    }

    private async ValueTask CheckPhaseAsync(string phase, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { if (beforeCommit is not null) await beforeCommit(phase, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new WorkerDockException("PersistenceUnavailable"); }
        ct.ThrowIfCancellationRequested();
    }

    private sealed record State(
        ImmutableDictionary<Guid, JobEditorDto> Jobs,
        ImmutableDictionary<(Guid JobId, long Revision), JobRevision> Revisions,
        ImmutableDictionary<Guid, MemoryExecution> Executions,
        ImmutableDictionary<Guid, CommandReceipt> Commands,
        ImmutableDictionary<(Guid JobId, string Mode, string Subject, string Key), DedupRecord> Dedup,
        ImmutableArray<MemoryJobAudit> Audits,
        ImmutableArray<MemoryJobOutbox> Outbox)
    {
        public ImmutableDictionary<Guid, string> CommandKinds { get; init; } = ImmutableDictionary<Guid, string>.Empty;
    }
    private sealed record DedupRecord(CommandReceipt Receipt, DateTimeOffset ExpiresUtc);
    private sealed record MemoryJobOutbox(Guid EventId, string Kind, Guid EntityId, long Revision,
        string PayloadDigest, DateTimeOffset AvailableUtc, string State = "Pending", DateTimeOffset? RetryAtUtc = null);
}

public sealed record MemoryJobAudit(Guid EventId, string EventType, Guid TargetId,
    long? BeforeRevision, long AfterRevision, string Outcome, string ActorSubject,
    string AuthenticationMode, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
