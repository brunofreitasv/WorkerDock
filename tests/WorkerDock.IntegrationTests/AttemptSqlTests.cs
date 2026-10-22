using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class AttemptSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_boots_stop_ordering_and_sql_fences_are_durable(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        var job = new JobRevision(Guid.NewGuid(), 0, "attempt", "workerdock.http", "1.0.0", JsonSerializer.SerializeToElement(new { }), null,
            new(null, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, ["Busy"], false, "Queue", 100, 5));
        var actor = new ActorContext("operator", "Anonymous", Guid.NewGuid());
        await commands.SaveAsync(job, null, actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        await SqlCapacityTestSetup.ReadyAsync(options,"boot-a","boot-b");
        var run = await commands.RunNowAsync(job.JobId, "sql-fence", actor, default);
        var projections = new SqlProjectionStore(options);
        var envelope = Assert.Single(await projections.ReadPendingAsync(10, default));
        var intent = (await projections.ReadAsync(envelope, 1, default)).Intent;
        var first = new SqlAttemptStore(options); var second = new SqlAttemptStore(options);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AttemptStart> Start(SqlAttemptStore store, string boot) { await barrier.Task; return await store.BeginAsync(intent, boot, default); }
        var a = Start(first, "boot-a"); var b = Start(second, "boot-b"); barrier.SetResult();
        var starts = await Task.WhenAll(a, b);
        var lease = Assert.Single(starts, x => x.Decision == "Started").Lease!;
        Assert.Single(starts, x => x.Decision == "AlreadyOwned");
        Assert.Null(await first.RenewAsync(lease with { Generation = lease.Generation + 1 }, default));
        Assert.Null(await first.RenewAsync(lease with { WorkerBootId = "other" }, default));
        var descriptor = await first.GetDescriptorAsync(lease, default);
        Assert.Equal(1, descriptor.AttemptOrdinal); Assert.Null(descriptor.DeadlineUtc);
        var receipt = await new SqlExecutionControls(options).StopAsync(run.TargetId, true, "confirmed force", actor, default);
        Assert.Equal(receipt, await commands.GetCommandAsync(receipt.CommandId, default));
        Assert.True((await second.ReadSignalAsync(lease, default)).StopRequested);
        var success = new TemplateResult("Success", "Ok", false, JsonSerializer.SerializeToElement(new { }), null);
        await first.CompleteAsync(lease, new(success, "Completed", true, false), default);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{run.TargetId}' AND State=N'Stopped' AND StopRequested=1"));
        Assert.Equal(0, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EntityId='{run.TargetId}' AND JSON_VALUE(PayloadJson,'$.attemptOrdinal')='2'"));
        Assert.Equal(0, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].JobPermits WHERE ExecutionId='{run.TargetId}'"));
        Assert.Equal("Completed", (await second.BeginAsync(intent, "boot-b", default)).Decision);

        var crash = await commands.RunNowAsync(job.JobId, "crash-before-launch", actor, default);
        var nextEnvelope = (await projections.ReadPendingAsync(10, default)).Single(x => x.EntityId == crash.TargetId);
        var nextIntent = (await projections.ReadAsync(nextEnvelope, 1, default)).Intent;
        var abandoned = (await first.BeginAsync(nextIntent, "boot-a", default)).Lease!;
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Attempts SET LeaseExpiresUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE AttemptId='{abandoned.AttemptId}'");
        Assert.Null(await second.RenewAsync(abandoned, default));
        var reviews = await Task.WhenAll(first.ReviewExpiredAsync(10, default).AsTask(), second.ReviewExpiredAsync(10, default).AsTask());
        Assert.Equal(1, reviews.Sum());
        await first.CompleteAsync(abandoned, new(success, "Completed", true, false), default);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{crash.TargetId}' AND State=N'NeedsReview' AND Certainty=N'Unknown'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AttemptStatusEvents WHERE ExecutionId='{crash.TargetId}' AND State=N'NeedsReview'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].JobPermits WHERE ExecutionId='{crash.TargetId}'"));
        var rejected = await Assert.ThrowsAsync<WorkerDockException>(async () => await fixture.Runner.RollbackAsync("tenant", "0003c_capacity", default));
        Assert.Equal("UnsafeRollback", rejected.Code);
    }

    [Fact]
    public async Task Empty_attempt_migration_round_trip_restores_schema()
    {
        await using var database = new SqlDatabaseFixture(); await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0003b_attempts", default);
        await runner.RollbackAsync("tenant", "0003b_attempts", default);
        await runner.ApplyAsync("tenant", "0003b_attempts", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock') AND name=N'AttemptStatusEvents'"));
    }
}
