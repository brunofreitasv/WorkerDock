using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Domain.Jobs;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.IntegrationTests;

public sealed class DynamicValuesEndToEndTests
{
    private static readonly ActorContext Actor = new("issuer:dynamic-e2e", "Entra", Guid.NewGuid());
    private static readonly DynamicValuePlan Plan = new(1, "UTC", [
        new("/referenceDate", DynamicValueSource.ReferenceDate, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate),
        new("/idempotencyKey", DynamicValueSource.IdempotencyKey, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.String)]);
    private static readonly ExecutionPolicy Policy = new(null, 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), .1, ["Busy"], false, "Queue", 10, 5);

    [Fact]
    public async Task Scheduled_occurrence_resolves_original_reference_date_and_retry_reuses_snapshot()
    {
        var clock = new MutableClock(new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero));
        var executionId = Guid.NewGuid();
        var key = "scheduled-execution-key";
        var context = ExecutionValueContextFactory.Scheduled(Plan, new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var store = Store(clock, executionId, key, context);

        var firstLease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var first = await store.GetDescriptorAsync(firstLease, default);
        Assert.Equal("2026-10-20", first.Configuration.GetProperty("referenceDate").GetString());
        Assert.Equal(key, first.Configuration.GetProperty("idempotencyKey").GetString());
        Assert.Equal("2026-10-20T09:00:00.0000000+00:00", first.ValueContext!.ScheduledForUtc!.Value.ToString("O"));

        await store.CompleteAsync(firstLease, Completion(), default);
        var retry = Assert.Single(store.Retries);
        clock.Now = new(2026, 10, 22, 1, 0, 0, TimeSpan.Zero);
        var retryLease = (await store.BeginAsync(retry, "boot", default)).Lease!;
        var second = await store.GetDescriptorAsync(retryLease, default);

        Assert.Equal("2026-10-20", second.Configuration.GetProperty("referenceDate").GetString());
        Assert.Equal(key, second.Configuration.GetProperty("idempotencyKey").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first.ValueSnapshot), JsonSerializer.SerializeToElement(second.ValueSnapshot)));
    }

    [Fact]
    public async Task Manual_override_flows_from_admission_to_resolved_configuration_without_revision_change()
    {
        var clock = new MutableClock(new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero));
        var descriptor = TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object","properties":{"referenceDate":{"type":"string","x-workerDock-dynamicValue":true}},"additionalProperties":false}""") with { Id = "custom.dynamic-e2e", Version = "1.1.0" });
        var catalog = new TemplateCatalog([descriptor]);
        await catalog.RegisterAsync([descriptor], default);
        var job = new JobRevision(Guid.NewGuid(), 0, "dynamic", descriptor.Id, descriptor.Version,
            JsonSerializer.SerializeToElement(new { referenceDate = "placeholder" }), null, Policy);
        var commands = new InMemoryJobCommands(clock, [job], templateCatalog: catalog);
        var revisionBeforeRun = (await commands.GetEditableAsync(job.JobId, default)).Revision.Revision;
        var accepted = await commands.RunNowAsync(job.JobId, "manual-e2e-key", new(new DateOnly(2026, 10, 19)), Actor, default);
        var context = commands.GetAcceptedValueContext(accepted.TargetId)!;
        var acceptedRevision = (await commands.GetEditableAsync(job.JobId, default)).Revision;
        var plan = Plan with { Bindings = [Plan.Bindings[0]] };
        var store = Store(clock, accepted.TargetId, "execution-idempotency-key", context, plan);

        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var prepared = await store.GetDescriptorAsync(lease, default);

        Assert.Equal(new DateOnly(2026, 10, 19), context.ReferenceDateOverride);
        Assert.Equal("2026-10-19", prepared.Configuration.GetProperty("referenceDate").GetString());
        Assert.Equal(revisionBeforeRun, acceptedRevision.Revision);
        Assert.Equal(job.TemplateVersion, acceptedRevision.TemplateVersion);
    }

    [Fact]
    public async Task Unavailable_dynamic_source_prevents_preparation_side_effects()
    {
        var clock = new MutableClock(new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero));
        var manual = new ExecutionValueContext(1, "UTC", "Manual", null, clock.Now, null, null);
        var scheduledOnly = Plan with { Bindings = [new("/referenceDate", DynamicValueSource.ScheduledTimestamp, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoUtcTimestamp)] };
        var targetCalls = 0;
        var store = Store(clock, Guid.NewGuid(), "invalid-source-key", manual, scheduledOnly,
            _ => { targetCalls++; return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { })); });

        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var prepared = await store.GetDescriptorAsync(lease, default);

        Assert.Equal("DynamicSourceUnavailable", prepared.ValueResolutionError);
        Assert.Null(prepared.ValueSnapshot);
        Assert.Equal(0, targetCalls);
    }

    private static InMemoryAttemptStore Store(MutableClock clock, Guid executionId, string key, ExecutionValueContext context,
        DynamicValuePlan? plan = null, Func<CancellationToken, ValueTask<JsonElement>>? integrationSnapshot = null)
    {
        var descriptor = new ExecutionDescriptor(executionId, 1, "custom.dynamic-e2e", "1.1.0", key,
            JsonSerializer.SerializeToElement(new { referenceDate = "placeholder", idempotencyKey = "placeholder" }), null, null,
            JsonSerializer.SerializeToElement(new { }), null, context);
        return new(clock, descriptor, Policy, integrationSnapshot: integrationSnapshot, dynamicValues: plan ?? Plan);
    }

    private static AttemptCompletion Completion() => new(new("TransientFailure", "Busy", false, JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
