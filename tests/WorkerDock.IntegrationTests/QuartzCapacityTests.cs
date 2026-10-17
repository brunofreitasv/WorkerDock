using WorkerDock.DatabaseMigrator;
using WorkerDock.Quartz.Hosting;
using Quartz;

namespace WorkerDock.IntegrationTests;

public sealed class QuartzCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Locked_acquisition_does_not_multiply_capacity(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        var publisher = await fixture.StartAsync("publisher");
        var workers = new[] { await fixture.StartAsync("execution"), await fixture.StartAsync("execution"), await fixture.StartAsync("execution") };
        var deliveries = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var delivery in deliveries) await fixture.PublishAsync(publisher, delivery);
        foreach (var delivery in deliveries)
        {
            var started = await fixture.NextStartedAsync();
            Assert.Contains(started.Delivery, deliveries);
            Assert.Equal(1, fixture.Active);
            await fixture.ReleaseAsync(started);
            await fixture.NextCompletedAsync();
        }
        Assert.Equal(1, fixture.MaximumActive);
        Assert.Equal(6, fixture.Deliveries.Count);
        Assert.All(fixture.Deliveries.Values, count => Assert.Equal(1, count));
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM dbo.WDS_QRTZ_JOB_DETAILS"));
        foreach (var worker in workers) await fixture.StopAsync(worker);
    }
}

public sealed class QuartzSqlTopologyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Publisher_restart_persists_one_delivery_for_two_worker_processes(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        var publisher = await fixture.StartAsync("publisher");
        var delivery = Guid.NewGuid().ToString("N");
        await fixture.PublishAsync(publisher, delivery);
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM dbo.WDE_QRTZ_TRIGGERS"));
        var refused = await Assert.ThrowsAsync<WorkerDock.Application.Common.WorkerDockException>(async () => await fixture.Runner.RollbackAsync("tenant", "0002_quartz", default));
        Assert.Equal("UnsafeRollback", refused.Code);
        await fixture.StopAsync(publisher);
        var restarted = await fixture.StartAsync("publisher");
        var worker1 = await fixture.StartAsync("execution");
        var worker2 = await fixture.StartAsync("execution");
        var started = await fixture.NextStartedAsync();
        Assert.Equal(delivery, started.Delivery);
        await fixture.ReleaseAsync(started);
        await fixture.NextCompletedAsync();
        await fixture.StopAsync(worker1);
        await fixture.StopAsync(worker2);
        await fixture.StopAsync(restarted);
        Assert.Single(fixture.Deliveries);
        Assert.Equal(1, fixture.Deliveries[delivery]);
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM dbo.WDE_QRTZ_TRIGGERS"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Schema_round_trip_and_runtime_DML_are_available_without_DDL(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        Assert.Equal(30, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'WD[SE][_]QRTZ[_]%'"));
        await fixture.Database.ExecuteAsync("CREATE USER QuartzRuntimeProbe WITHOUT LOGIN; ALTER ROLE WorkerDockRuntime ADD MEMBER QuartzRuntimeProbe;");
        foreach (var prefix in new[] { "WDS_QRTZ_", "WDE_QRTZ_" })
            await fixture.Database.ExecuteAsync($"EXECUTE AS USER = 'QuartzRuntimeProbe'; INSERT dbo.{prefix}LOCKS(SCHED_NAME,LOCK_NAME) VALUES ('permission-probe','probe'); UPDATE dbo.{prefix}LOCKS SET LOCK_NAME='changed' WHERE SCHED_NAME='permission-probe'; SELECT * FROM dbo.{prefix}LOCKS; DELETE dbo.{prefix}LOCKS WHERE SCHED_NAME='permission-probe'; IF HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE')<>0 THROW 51004,'Unexpected DDL permission',1; REVERT;");
        await fixture.Database.ExecuteAsync("ALTER ROLE WorkerDockRuntime DROP MEMBER QuartzRuntimeProbe; DROP USER QuartzRuntimeProbe;");
        await fixture.Runner.RollbackAsync("tenant", "0002_quartz", default);
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'WD[SE][_]QRTZ[_]%'"));
        await fixture.Runner.ApplyAsync("tenant", "0002_quartz", default);
        await using var topology = new QuartzTopology(new(fixture.Database.ConnectionString, fixture.Database.InstanceId));
        var scheduler = await topology.CreateExecutionAsync(new(fixture.Database.InstanceId, Guid.NewGuid().ToString("N"), 1), default);
        Assert.Equal(SchedulerStatus.Created, scheduler.Status);
    }
}
