using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Jobs;
using WorkerDock.Contracts.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;
namespace WorkerDock.IntegrationTests;

public sealed class CommandOutcomeTests
{
    internal static readonly ActorContext Actor = new("operator", "Anonymous", Guid.NewGuid());
    internal static JobRevision Job() => new(Guid.NewGuid(), 0, "command outcomes", "workerdock.http", "1.0.0",
        JsonSerializer.SerializeToElement(new { secret = "never-in-command-status" }), null,
        new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 10, 5));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Memory_publication_receipts_survive_failure_and_lost_ack(bool rejected)
    {
        var job = Job(); var store = new InMemoryJobCommands(TimeProvider.System, [job], templateCatalog: JobCommandTestCatalog.Create());
        await AssertPublication(store, store, job.JobId, rejected);
        var settled = store.AuditEvents.Where(row => row.EventType == "CommandSettled").ToArray();
        Assert.Single(settled);
        Assert.DoesNotContain("never-in-command-status", JsonSerializer.Serialize(settled));
    }

    internal static async Task AssertPublication(IJobCommands commands, IProjectionStore projections, Guid jobId, bool rejected)
    {
        var run = await commands.RunNowAsync(jobId, "publication", Actor, default);
        var envelope = Assert.Single(await projections.ReadPendingAsync(20, default));
        Assert.Equal("Pending", (await commands.GetCommandAsync(run.CommandId, default)).State);
        await projections.RecordFailureAsync(envelope.IntentId, rejected ? "ProjectionIntegrityError" : "QuartzStoreUnavailable", DateTimeOffset.UtcNow.AddSeconds(-1), default);
        Assert.Equal(rejected ? "Failed" : "Pending", (await commands.GetCommandAsync(run.CommandId, default)).State);
        // The publication commit succeeds, but the caller loses its response and retries the same acknowledgement.
        await projections.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, default);
        await projections.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, default);
        var current = await commands.GetCommandAsync(run.CommandId, default);
        Assert.Equal(rejected ? "Failed" : "Applied", current.State);
        Assert.Equal(current, await commands.RunNowAsync(jobId, "publication", Actor, default));
        Assert.Empty(await projections.ReadPendingAsync(20, default));
        Assert.DoesNotContain("never-in-command-status", JsonSerializer.Serialize(current));
    }

    [Fact]
    public async Task Memory_failed_audit_rolls_back_acknowledgement_and_command_outcome()
    {
        var fail = false; var job = Job();
        var store = new InMemoryJobCommands(TimeProvider.System, [job], (phase, _) =>
            fail && phase == "Audit" ? ValueTask.FromException(new Exception("secret")) : ValueTask.CompletedTask, templateCatalog: JobCommandTestCatalog.Create());
        var run = await store.RunNowAsync(job.JobId, "audit", Actor, default);
        var envelope = Assert.Single(await store.ReadPendingAsync(10, default)); fail = true;
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await store.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, default));
        Assert.Equal("PersistenceUnavailable", error.Code);
        Assert.Equal("Pending", (await store.GetCommandAsync(run.CommandId, default)).State);
        Assert.Single(await store.ReadPendingAsync(10, default)); Assert.Single(store.AuditEvents);
        fail = false; await store.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, default);
        Assert.Equal("Applied", (await store.GetCommandAsync(run.CommandId, default)).State);
    }
}

[Trait("Store", "Sql")]
public sealed class CommandOutcomeSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_unconfirmed_running_stop_fails_and_stale_completion_cannot_apply_it(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options, "command-worker");
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var run = await commands.RunNowAsync(job.JobId, "unknown-stop", CommandOutcomeTests.Actor, default);
        var projections = new SqlProjectionStore(options); var envelope = Assert.Single(await projections.ReadPendingAsync(10, default));
        var intent = (await projections.ReadAsync(envelope, 1, default)).Intent;
        var attempts = new SqlAttemptStore(options); var lease = (await attempts.BeginAsync(intent, "command-worker", default)).Lease!;
        var stop = await new SqlExecutionControls(options).StopAsync(run.TargetId, true, "terminate", CommandOutcomeTests.Actor, default);
        Assert.Equal("Pending", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        await attempts.CompleteAsync(lease, new(null, "ProcessFailed", false, true), default);
        Assert.Equal("Failed", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        await attempts.CompleteAsync(lease, new(new TemplateResult("Success", "Ok", false, JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);
        Assert.Equal("Failed", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{run.TargetId}' AND State=N'NeedsReview'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_ack_audit_failure_rolls_back_publication_and_command_outcome(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options);
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var run = await commands.RunNowAsync(job.JobId, "audit", CommandOutcomeTests.Actor, default);
        var projections = new SqlProjectionStore(options); var envelope = Assert.Single(await projections.ReadPendingAsync(10, default));
        await fixture.Database.ExecuteAsync("CREATE TRIGGER [workerdock].TestCommandAuditFailure ON [workerdock].AuditEvents AFTER INSERT AS THROW 51000, 'Injected audit failure', 1;");
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await projections.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, default));
        Assert.Equal("ProjectionStoreUnavailable", error.Code);
        Assert.Equal("Pending", (await commands.GetCommandAsync(run.CommandId, default)).State);
        Assert.Single(await projections.ReadPendingAsync(10, default));
        await fixture.Database.ExecuteAsync("DROP TRIGGER [workerdock].TestCommandAuditFailure");
        await projections.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, default);
        Assert.Equal("Applied", (await commands.GetCommandAsync(run.CommandId, default)).State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Sql_publication_receipts_match_memory_and_survive_lost_ack(bool snapshot, bool rejected)
    {
        await using var fixture = new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options);
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        await CommandOutcomeTests.AssertPublication(commands, new SqlProjectionStore(options), job.JobId, rejected);
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventType=N'CommandSettled'"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Sql_stop_completion_ordering_settles_confirmed_stop_only(bool snapshot, bool stopFirst)
    {
        await using var fixture = new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options, "command-worker");
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var run = await commands.RunNowAsync(job.JobId, "stop", CommandOutcomeTests.Actor, default);
        var projections = new SqlProjectionStore(options);
        var envelope = Assert.Single(await projections.ReadPendingAsync(10, default));
        var intent = (await projections.ReadAsync(envelope, 1, default)).Intent;
        var attempts = new SqlAttemptStore(options); var controls = new SqlExecutionControls(options);
        var lease = (await attempts.BeginAsync(intent, "command-worker", default)).Lease!;
        Assert.Equal("Applied", (await commands.GetCommandAsync(run.CommandId, default)).State);
        CommandReceipt? stop = null;
        if (stopFirst)
        {
            stop = await controls.StopAsync(run.TargetId, true, "confirmed", CommandOutcomeTests.Actor, default);
            Assert.Equal("Pending", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        }
        await attempts.CompleteAsync(lease, new(new TemplateResult("Success", "Ok", false, JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);
        stop ??= await controls.StopAsync(run.TargetId, true, "confirmed", CommandOutcomeTests.Actor, default);
        Assert.Equal("Applied", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{run.TargetId}' AND State=N'{(stopFirst ? "Stopped" : "Succeeded")}'"));
    }
}
