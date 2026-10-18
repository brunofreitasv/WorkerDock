using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class ProjectionSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_cursor_survives_restart_and_bounds_large_job_backlog(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options);
        var commands = new SqlJobCommands(options);
        var first = Job() with { JobId = Guid.Parse("00000000-0000-0000-0000-000000000001"), Policy = Job().Policy with { BacklogLimit = 10000 } };
        var second = Job() with { JobId = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        foreach (var job in new[] { first, second })
        {
            await commands.SaveAsync(job, null, Actor(), default);
            await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
            await commands.RunNowAsync(job.JobId, "maintenance", Actor(), default);
        }
        await fixture.Database.ExecuteAsync($"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit)
                SELECT TOP (5000) NEWID(),NULL,1,0,1 FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
                SELECT TOP (25000) NEWID(),'{first.JobId}',1,N'Manual',CONVERT(nvarchar(100),NEWID()),N'Queued',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME()
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """);
        var store = new SqlProjectionStore(options);
        // Outbox reads do not visit 5,000 idle jobs or perform admission ahead of existing publication.
        Assert.Equal(2, (await store.ReadPendingAsync(10, default)).Count);
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].ProjectionMaintenance WHERE AfterJobId IS NULL"));
        await store.MaintainAsync(default);
        Assert.Equal(32, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Executions WHERE State=N'Skipped'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].ProjectionMaintenance WHERE AfterJobId='{first.JobId}'"));
        await new SqlProjectionStore(options).MaintainAsync(default);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].ProjectionMaintenance WHERE AfterJobId='{second.JobId}'"));
        await new SqlProjectionStore(options).MaintainAsync(default);
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].ProjectionMaintenance WHERE AfterJobId IS NULL"));
        await store.MaintainAsync(default);
        Assert.Equal(64, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Executions WHERE State=N'Skipped'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_survives_sql_handle_restart_and_concurrent_publishers(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        await SqlCapacityTestSetup.ReadyAsync(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId));
        var commands = new SqlJobCommands(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId));
        var job = Job();
        await commands.SaveAsync(job, null, Actor(), default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var receipt = await commands.RunNowAsync(job.JobId, "publication", Actor(), default);
        var store = new SqlProjectionStore(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId));
        var envelope = Assert.Single(await store.ReadPendingAsync(10, default));
        Assert.Equal(receipt.TargetId, envelope.EntityId);
        var state = await store.ReadAsync(envelope, 1, default);
        await using (var first = new QuartzTopology(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId)))
        {
            var handle = await first.CreateExecutionPublisherAsync(new(fixture.Database.InstanceId, "first", 1), default);
            await new QuartzPublisher(handle, typeof(PublicationRecoveryTests.PublicationProbe)).PublishAsync(state.Intent, default);
            // Simulate loss of the application acknowledgement after durable Quartz commit.
        }
        await using var restarted = new QuartzTopology(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId));
        var publisherHandle = await restarted.CreateExecutionPublisherAsync(new(fixture.Database.InstanceId, "restarted", 1), default);
        var publisher = new QuartzPublisher(publisherHandle, typeof(PublicationRecoveryTests.PublicationProbe));
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => publisher.PublishAsync(state.Intent, default).AsTask()));
        Assert.Equal(1, await new DispatchReconciler(store, store, publisher, TimeProvider.System).ReconcileAsync(20, default));
        Assert.Empty(await store.ReadPendingAsync(10, default));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_JOB_DETAILS"));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_TRIGGERS"));
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Executions SET State=N'Succeeded' WHERE ExecutionId='{envelope.EntityId}'; UPDATE [workerdock].Outbox SET ReviewAtUtc=NULL;");
        Assert.Equal(1, await new DispatchReconciler(store, store, publisher, TimeProvider.System).ReconcileAsync(20, default));
        Assert.Empty(await store.ReadPublishedAsync(10, default));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Outbox WHERE ProjectionState=N'Retired' AND DispatchedAtUtc IS NOT NULL"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_projection_ack_integrity_and_retry_deadline_are_persisted(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant", "0003d_dispatch", default);
        var store = new SqlProjectionStore(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId));
        var id = Guid.NewGuid();
        var entity = Guid.NewGuid();
        var digest = ProjectionEnvelope.ExecutionDigest(entity, 1);
        await fixture.Database.ExecuteAsync($"INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,EntityId,EntityRevision,PayloadDigest) VALUES('{id}',N'Unknown',N'{{}}',SYSUTCDATETIME(),SYSUTCDATETIME(),'{entity}',1,'{digest}')");
        var envelope = Assert.Single(await store.ReadPendingAsync(10, default));
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await store.AcknowledgeAsync(envelope.IntentId, new string('0',64), default));
        Assert.Equal("ProjectionIntegrityError", error.Code);
        await store.RecordFailureAsync(id, "UnsupportedProjection", DateTimeOffset.UtcNow.AddHours(1), default);
        Assert.Empty(await store.ReadPendingAsync(10, default));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Outbox WHERE ProjectionState=N'Failed' AND LastErrorCode=N'UnsupportedProjection' AND DispatchedAtUtc IS NULL AND RetryAtUtc>SYSUTCDATETIME()"));
        var wrongTenant = new SqlProjectionStore(new(fixture.RuntimeConnectionString, Guid.NewGuid()));
        var mismatch = await Assert.ThrowsAsync<WorkerDockException>(async () => await wrongTenant.ReadPendingAsync(10, default));
        Assert.Equal("TenantInstanceMismatch", mismatch.Code);
    }

    [Fact]
    public async Task Projection_migration_is_paired_and_guards_live_reconciliation_metadata()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0003a_projection", default);
        await runner.RollbackAsync("tenant", "0003a_projection", default);
        await runner.ApplyAsync("tenant", "0003a_projection", default);
        await database.ExecuteAsync("INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,LastErrorCode) VALUES(NEWID(),N'Unknown',N'{}',SYSUTCDATETIME(),SYSUTCDATETIME(),N'UnsupportedProjection')");
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0003a_projection", default));
        Assert.Equal("UnsafeRollback", error.Code);
    }

    private static ActorContext Actor() => new("issuer:subject", "Entra", Guid.NewGuid());
    private static JobRevision Job() => new(Guid.NewGuid(), 0, "Publication SQL fixture", "workerdock.http", "1.0.0",
        JsonSerializer.SerializeToElement(new { secret = "never-in-quartz" }), null,
        new(null, 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 0.1, ["Transient"], false, "Queue", 10, 5));
}
