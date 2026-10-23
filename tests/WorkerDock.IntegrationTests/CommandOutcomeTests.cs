using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Contracts.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;
namespace WorkerDock.IntegrationTests;

internal static class CommandOutcomeTests
{
    internal static readonly ActorContext Actor = new("operator", "Anonymous", Guid.NewGuid());
    internal static JobRevision Job() => new(Guid.NewGuid(), 0, "command outcomes", "workerdock.http", "1.1.0",
        JsonSerializer.SerializeToElement(new { secret = "never-in-command-status" }), null,
        new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 10, 5));

}

[Trait("Store", "Sql")]
public sealed class CommandOutcomeSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_unconfirmed_running_stop_fails_and_stale_completion_cannot_apply_it(bool snapshot)
    {
        await using var fixture = new TenantSqlFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0001_initial", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        await SqlCapacityTestSetup.ReadyAsync(options, "command-worker");
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var run = await commands.RunNowAsync(job.JobId, "unknown-stop", CommandOutcomeTests.Actor, default);
        var attempts = new SqlAttemptStore(options); var lease = (await attempts.BeginAsync(run.TargetId, "command-worker", default)).Lease!;
        var stop = await new SqlExecutionControls(options).StopAsync(run.TargetId, true, "terminate", CommandOutcomeTests.Actor, default);
        Assert.Equal("Pending", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        await attempts.CompleteAsync(lease, new(null, "ProcessFailed", false, true), default);
        Assert.Equal("Failed", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        await attempts.CompleteAsync(lease, new(new TemplateResult("Success", "Ok", false, JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);
        Assert.Equal("Failed", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{run.TargetId}' AND State=N'NeedsReview'"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Sql_stop_completion_ordering_settles_confirmed_stop_only(bool snapshot, bool stopFirst)
    {
        await using var fixture = new TenantSqlFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0001_initial", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        await SqlCapacityTestSetup.ReadyAsync(options, "command-worker");
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var run = await commands.RunNowAsync(job.JobId, "stop", CommandOutcomeTests.Actor, default);
        var attempts = new SqlAttemptStore(options); var controls = new SqlExecutionControls(options);
        var lease = (await attempts.BeginAsync(run.TargetId, "command-worker", default)).Lease!;
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

[Trait("Store", "Sql")]
public sealed class ManualRequestSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRequestRetry_ReturnsOriginalExecution(bool snapshot)
    {
        await using var fixture = new TenantSqlFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0001_initial", default);
        await fixture.Runner.ApplyAsync("tenant", "0001_initial", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create()); var job = CommandOutcomeTests.Job();
        await commands.SaveAsync(job, null, CommandOutcomeTests.Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");

        var first = await commands.RunNowAsync(job.JobId, "same-request", CommandOutcomeTests.Actor, default);
        var expiresAfterSeconds = await fixture.Database.ScalarAsync($"SELECT DATEDIFF_BIG(second,CreatedAtUtc,ManualRequestExpiresAtUtc) FROM [workerdock].Executions WHERE ExecutionId='{first.TargetId}'");
        var retry = await commands.RunNowAsync(job.JobId, "same-request", CommandOutcomeTests.Actor, default);

        Assert.Equal(86_400, expiresAfterSeconds);
        Assert.Equal(first.TargetId, retry.TargetId);
        Assert.Equal(first.CommandId, retry.CommandId);
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Executions"));
    }
}
