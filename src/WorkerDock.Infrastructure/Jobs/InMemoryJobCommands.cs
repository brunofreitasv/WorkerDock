using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Jobs;

namespace WorkerDock.Infrastructure.Jobs;

public sealed record MemoryExecution(Guid ExecutionId, Guid JobId, long JobRevision, Guid? ProfileId,
    string State, string IdempotencyKey);

public sealed class InMemoryJobCommands : IJobCommands
{
    private readonly TimeProvider clock;
    private readonly Func<string, CancellationToken, ValueTask>? beforeCommit;
    private readonly SemaphoreSlim gate = new(1, 1);
    private State state = new([], [], [], [], [], [], []);

    public InMemoryJobCommands(TimeProvider clock, IEnumerable<JobRevision>? enabledSeeds = null,
        Func<string, CancellationToken, ValueTask>? beforeCommit = null,
        IEnumerable<JobRevision>? archivedSeeds = null)
    {
        this.clock = clock;
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
    public int OutboxCount => Volatile.Read(ref state).Outbox.Length;
    public int ExecutionCount => Volatile.Read(ref state).Executions.Count;
    public MemoryExecution? GetExecution(Guid id) => Volatile.Read(ref state).Executions.GetValueOrDefault(id);
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
            var audit = new MemoryJobAudit(Guid.NewGuid(), "JobRevisionSaved", proposed.JobId, number, actor.Subject,
                actor.CorrelationId, clock.GetUtcNow());
            Volatile.Write(ref state, before with
            {
                Jobs = before.Jobs.SetItem(proposed.JobId, next),
                Revisions = before.Revisions.Add((proposed.JobId, number), revision),
                Commands = before.Commands.Add(receipt.CommandId, receipt),
                Audits = before.Audits.Add(audit)
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
                return old.Receipt;
            if (!before.Jobs.TryGetValue(jobId, out var editor)) throw new WorkerDockException("JobNotFound");
            if (editor.Archived) throw new WorkerDockException("JobArchived");
            if (!editor.Enabled) throw new WorkerDockException("JobDisabled");
            if (editor.Revision.ProfileId is not null) throw new WorkerDockException("ProfileUnavailable");
            var executionId = Guid.NewGuid();
            var receipt = new CommandReceipt(Guid.NewGuid(), executionId, "Pending");
            var execution = new MemoryExecution(executionId, jobId, editor.Revision.Revision,
                editor.Revision.ProfileId, "Queued", executionId.ToString("N"));
            await CheckPhaseAsync("Audit", ct);
            await CheckPhaseAsync("Outbox", ct);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ExecutionDispatch|" + executionId.ToString("N"))));
            Volatile.Write(ref state, before with
            {
                Executions = before.Executions.Add(executionId, execution),
                Commands = before.Commands.Add(receipt.CommandId, receipt),
                Dedup = before.Dedup.SetItem(scope, new(receipt, now.AddHours(24))),
                Audits = before.Audits.Add(new(Guid.NewGuid(), "ManualRunAccepted", executionId,
                    editor.Revision.Revision, actor.Subject, actor.CorrelationId, now)),
                Outbox = before.Outbox.Add(new(Guid.NewGuid(), "ExecutionDispatch", executionId,
                    editor.Revision.Revision, digest, now))
            });
            return receipt;
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
        ImmutableArray<MemoryJobOutbox> Outbox);
    private sealed record DedupRecord(CommandReceipt Receipt, DateTimeOffset ExpiresUtc);
    private sealed record MemoryJobAudit(Guid EventId, string EventType, Guid TargetId, long Revision,
        string ActorSubject, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
    private sealed record MemoryJobOutbox(Guid EventId, string Kind, Guid EntityId, long Revision,
        string PayloadDigest, DateTimeOffset AvailableUtc);
}
