using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Contracts.Values;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.IntegrationTests;

public sealed class DynamicValueAdmissionTests
{
    private static readonly ActorContext Actor = new("issuer:admission", "Entra", Guid.NewGuid());

    [Fact]
    public void Late_occurrence_pins_original_time()
    {
        var occurrence = new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero);
        var context = ExecutionValueContextFactory.Scheduled(DynamicValueJson.EmptyPlan with { TimeZoneId = "America/Sao_Paulo" }, occurrence);
        Assert.Equal(occurrence, context.ScheduledForUtc);
        Assert.Equal("America/Sao_Paulo", context.TimeZoneId);
        Assert.Null(context.RequestedAtUtc);
    }

    [Fact]
    public async Task Manual_override_is_execution_scoped_and_same_key_conflicts_on_changed_date()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero));
        var descriptor = TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object","properties":{"date":{"type":"string","x-workerDock-dynamicValue":true}},"additionalProperties":false}""") with { Id = "custom.dynamic", Version = "1.1.0" });
        var catalog = new TemplateCatalog([descriptor]);
        await catalog.RegisterAsync([descriptor], default);
        var job = new JobRevision(Guid.NewGuid(), 0, "dynamic", descriptor.Id, descriptor.Version,
            JsonDocument.Parse("{}").RootElement.Clone(), null,
            new(null, 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 0.1, ["Transient"], false, "Queue", 10, 5));
        var commands = new InMemoryJobCommands(clock, [job], templateCatalog: catalog);
        var first = await commands.RunNowAsync(job.JobId, "same-key", new(new DateOnly(2026, 10, 19)), Actor, default);
        Assert.Equal(new DateOnly(2026, 10, 19), commands.GetAcceptedValueContext(first.TargetId)!.ReferenceDateOverride);
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        var repeated = await commands.RunNowAsync(job.JobId, "same-key", new(new DateOnly(2026, 10, 19)), Actor, default);
        Assert.Equal(first.TargetId, repeated.TargetId);
        var conflict = await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.RunNowAsync(job.JobId, "same-key", new(new DateOnly(2026, 10, 20)), Actor, default));
        Assert.Equal("RequestKeyConflict", conflict.Code);
        var next = await commands.RunNowAsync(job.JobId, "another-key", new ManualRunOptions(), Actor, default);
        Assert.Equal(new DateOnly(2026, 10, 22), DateOnly.FromDateTime(commands.GetAcceptedValueContext(next.TargetId)!.RequestedAtUtc!.Value.UtcDateTime));
        Assert.Equal(1, (await commands.GetEditableAsync(job.JobId, default)).Revision.Revision);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
