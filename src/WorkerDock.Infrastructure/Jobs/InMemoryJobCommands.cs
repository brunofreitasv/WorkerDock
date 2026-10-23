using WorkerDock.Application.Connections;
using System.Collections.Immutable;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Values;
using System.Text.Json;
using WorkerDock.Domain.Jobs;
using WorkerDock.Application.Settings;
using WorkerDock.Application.Templates;

namespace WorkerDock.Infrastructure.Jobs;

public sealed record MemoryExecution(Guid ExecutionId, Guid JobId, long JobRevision, Guid? ProfileId,
    string State, string IdempotencyKey, WorkerDock.Contracts.Values.ExecutionValueContext? ValueContext = null);

public sealed class InMemoryJobCommands : IJobCommands, IExecutionControls
{
    private readonly TimeProvider clock;
    private readonly IConnectionProfiles? profiles;
    private readonly IIntegrationSettings? integrationSettings;
    private readonly ITemplateCatalog? templateCatalog;
    private readonly ITemplateAuthoringService? authoring;
    private readonly Func<string, CancellationToken, ValueTask>? beforeCommit;
    private readonly SemaphoreSlim gate = new(1, 1);
    private State state = new([], [], [], [], [], []);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, InMemoryAttemptStore> attemptStores = new();

    public InMemoryJobCommands(TimeProvider clock, IEnumerable<JobRevision>? enabledSeeds = null,
        Func<string, CancellationToken, ValueTask>? beforeCommit = null,
        IEnumerable<JobRevision>? archivedSeeds = null, IConnectionProfiles? profiles = null, IIntegrationSettings? integrationSettings = null, ITemplateCatalog? templateCatalog = null, ITemplateAuthoringService? authoring = null)
    {
        this.clock = clock; this.profiles=profiles; this.integrationSettings=integrationSettings; this.templateCatalog=templateCatalog; this.authoring=authoring;
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
    public int ExecutionCount => Volatile.Read(ref state).Executions.Count;
    public MemoryExecution? GetExecution(Guid id)
    {
        var execution = Volatile.Read(ref state).Executions.GetValueOrDefault(id);
        return execution is not null && attemptStores.TryGetValue(id, out var attempts)
            ? execution with { State = attempts.Snapshot.State } : execution;
    }
    public InMemoryAttemptStore? GetAttemptStore(Guid id) => attemptStores.GetValueOrDefault(id);
    public WorkerDock.Contracts.Values.ExecutionValueContext? GetAcceptedValueContext(Guid id) =>
        attemptStores.TryGetValue(id, out var store) ? store.AcceptedDescriptor.ValueContext : null;
    public JobRevision? GetRevision(Guid jobId, long revision) => Volatile.Read(ref state).Revisions.GetValueOrDefault((jobId, revision));

    public async ValueTask<CommandReceipt> SaveAsync(JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        JobCommandValidation.ValidateRevision(proposed);
        await TemplateJobValidation.RequireRegisteredAsync(templateCatalog,proposed.TemplateId,proposed.TemplateVersion,ct);
        await TemplateJobValidation.ValidateBindingsAsync(templateCatalog,proposed,ct);
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

    public async ValueTask<CommandReceipt> EnableManualAsync(Guid jobId,byte[] expectedVersion,ActorContext actor,CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        if(jobId==Guid.Empty || expectedVersion.Length!=8) throw new WorkerDockException("ValidationFailed","version");
        await gate.WaitAsync(ct);
        try
        {
            var before=state;
            if(!before.Jobs.TryGetValue(jobId,out var editor)) throw new WorkerDockException("JobNotFound");
            if(!editor.Version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
            if(editor.Archived) throw new WorkerDockException("JobArchived");
            await TemplateJobValidation.ValidateAsync(templateCatalog,editor.Revision,ct,authoring,"Manual");
            if(editor.Revision.ProfileId is { } profileId)
            {
                var profile=profiles is null ? throw new WorkerDockException("ProfileUnavailable") : await profiles.GetCurrentAsync(profileId,ct);
                if(profile.Retired) throw new WorkerDockException("ProfileUnavailable");
            }
            var version=BitConverter.GetBytes(BitConverter.ToInt64(editor.Version)+1);
            var next=editor with { Enabled=true, Version=version };
            var receipt=new CommandReceipt(Guid.NewGuid(),jobId,"Applied");
            await CheckPhaseAsync("Audit",ct);
            ImmutableInterlocked.Update(ref state,current=>before with
            {
                Jobs=before.Jobs.SetItem(jobId,next),
                Commands=current.Commands.Add(receipt.CommandId,receipt),
                CommandKinds=current.CommandKinds.Add(receipt.CommandId,"EnableManual"),
                Audits=current.Audits.Add(new(Guid.NewGuid(),"JobEnabled",jobId,editor.Revision.Revision,editor.Revision.Revision,"Applied",actor.Subject,actor.AuthenticationMode,actor.CorrelationId,clock.GetUtcNow()))
            });
            return receipt;
        }
        finally { gate.Release(); }
    }

    public ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ActorContext actor, CancellationToken ct) =>
        RunNowAsync(jobId, requestKey, new ManualRunOptions(), actor, ct);

    public async ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ManualRunOptions options, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateRequestKey(jobId, requestKey, actor);
        if (options is null) throw new WorkerDockException("ValidationFailed", "referenceDateOverride");
        await gate.WaitAsync(ct);
        try
        {
            var before = state;
            var now = clock.GetUtcNow();
            var scope = (jobId, actor.AuthenticationMode, actor.Subject, requestKey);
            var identity = ManualRequestIdentity(jobId, options.ReferenceDateOverride);
            if (before.Dedup.TryGetValue(scope, out var old) && now < old.ExpiresUtc)
            {
                if (old.RequestHash != identity) throw new WorkerDockException("RequestKeyConflict");
                return before.Commands.GetValueOrDefault(old.Receipt.CommandId, old.Receipt);
            }
            if (!before.Jobs.TryGetValue(jobId, out var editor)) throw new WorkerDockException("JobNotFound");
            if (editor.Archived) throw new WorkerDockException("JobArchived");
            if (!editor.Enabled) throw new WorkerDockException("JobDisabled");
            var plan = editor.Revision.DynamicValues is { } dynamicJson
                ? WorkerDock.Contracts.Values.DynamicValueJson.Read(dynamicJson)
                : WorkerDock.Contracts.Values.DynamicValueJson.EmptyPlan;
            var capable = await HasDynamicCapabilityAsync(editor.Revision, ct);
            if (options.ReferenceDateOverride is not null && !capable)
                throw new WorkerDockException("ValidationFailed", "referenceDateOverride");
            await TemplateJobValidation.ValidateAsync(templateCatalog,editor.Revision,ct,authoring,"Manual");
            if (plan.Bindings.Any(binding => binding.Source == WorkerDock.Contracts.Values.DynamicValueSource.ScheduledTimestamp))
                throw new WorkerDockException("ValidationFailed", "configuration.dynamicValues");
            WorkerDock.Contracts.Connections.ConnectionProfileRevision? pinnedProfile=null;
            if(editor.Revision.ProfileId is {} profileId)
                pinnedProfile=profiles is not null ? (await profiles.GetCurrentAsync(profileId,ct)).Current : throw new WorkerDockException("ProfileUnavailable");
            var integration = JsonSerializer.SerializeToElement(new { version = 1, vaultRevision = 1, providerId = "workerdock.environment", publicOptions = new { }, policies = Array.Empty<object>() });
            var executionId = Guid.NewGuid();
            var valueContext = capable ? ExecutionValueContextFactory.Manual(plan, now, options.ReferenceDateOverride) : null;
            var receipt = new CommandReceipt(Guid.NewGuid(), executionId, "Pending");
            var execution = new MemoryExecution(executionId, jobId, editor.Revision.Revision,
                editor.Revision.ProfileId, "Queued", executionId.ToString("N"), valueContext);
            await CheckPhaseAsync("Audit", ct);
            ImmutableInterlocked.Update(ref state, current => before with
            {
                Executions = before.Executions.Add(executionId, execution),
                Commands = current.Commands.Add(receipt.CommandId, receipt),
                CommandKinds = current.CommandKinds.Add(receipt.CommandId, "ManualRun"),
                Dedup = before.Dedup.SetItem(scope, new(receipt, now.AddHours(24), identity)),
                Audits = current.Audits.Add(new(Guid.NewGuid(), "ManualRunAccepted", executionId,
                    editor.Revision.Revision, editor.Revision.Revision, "Accepted", actor.Subject,
                    actor.AuthenticationMode, actor.CorrelationId, now)),
            });
            attemptStores[executionId] = new InMemoryAttemptStore(clock,
                new ExecutionDescriptor(executionId, 1, editor.Revision.TemplateId, editor.Revision.TemplateVersion,
                    execution.IdempotencyKey, editor.Revision.Configuration, pinnedProfile?.ProfileRevisionId, pinnedProfile is null ? null : JsonSerializer.SerializeToElement(pinnedProfile), integration, null, valueContext), editor.Revision.Policy,
                committed: snapshot => SettleCommands(executionId, snapshot),
                integrationSnapshot: integrationSettings is null ? null : async token => IntegrationConfigurationJson.Serialize(await integrationSettings.GetSnapshotAsync(token)),
                dynamicValues: plan);
            return receipt;
        }
        finally { gate.Release(); }
    }

    private async ValueTask<bool> HasDynamicCapabilityAsync(JobRevision revision, CancellationToken ct)
    {
        if (templateCatalog is null) return false;
        var descriptor = await templateCatalog.GetAsync(revision.TemplateId, revision.TemplateVersion, ct);
        if (descriptor is null) return false;
        return ContainsDynamicAnnotation(descriptor.ConfigurationSchema);
    }

    private static bool ContainsDynamicAnnotation(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.NameEquals("x-workerDock-dynamicValue") && property.Value.ValueKind == JsonValueKind.True || ContainsDynamicAnnotation(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsDynamicAnnotation),
        _ => false
    };

    private static string ManualRequestIdentity(Guid jobId, DateOnly? date) => date is null
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("RunNow|" + jobId.ToString("N"))))
        : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("RunNow|" + jobId.ToString("N") + "|" + date.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))));

    private void SettleCommands(Guid executionId, AttemptSnapshot snapshot) =>
        ImmutableInterlocked.Update(ref state, before => Settle(before, executionId, snapshot));

    private State Settle(State before, Guid executionId, AttemptSnapshot snapshot, bool rejected = false)
    {
        var commands = before.Commands; var audits = before.Audits;
        foreach (var receipt in before.Commands.Values.Where(row => row.TargetId == executionId && row.State == "Pending"))
        {
            var manual = before.CommandKinds.GetValueOrDefault(receipt.CommandId) == "ManualRun";
            var outcome = manual ? snapshot.Ordinal > 0 ? "Applied" : rejected || snapshot.State is "Stopped" or "Skipped" or "Failed" ? "Failed" : "Pending"
                : snapshot.State == "Running" ? "Pending" : snapshot.State == "NeedsReview" ? "Failed" : "Applied";
            if (outcome == "Pending") continue;
            commands = commands.SetItem(receipt.CommandId, receipt with { State = outcome });
            audits = audits.Add(new(Guid.NewGuid(), "CommandSettled", executionId, null, 1, outcome,
                "workerdock", "System", receipt.CommandId, clock.GetUtcNow()));
        }
        return before with { Commands = commands, Audits = audits };
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
        ImmutableArray<MemoryJobAudit> Audits)
    {
        public ImmutableDictionary<Guid, string> CommandKinds { get; init; } = ImmutableDictionary<Guid, string>.Empty;
    }
    private sealed record DedupRecord(CommandReceipt Receipt, DateTimeOffset ExpiresUtc, string RequestHash);
}

public sealed record MemoryJobAudit(Guid EventId, string EventType, Guid TargetId,
    long? BeforeRevision, long AfterRevision, string Outcome, string ActorSubject,
    string AuthenticationMode, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
