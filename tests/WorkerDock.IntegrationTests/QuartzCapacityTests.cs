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
        await Task.WhenAll(workers.Select(fixture.ArmAsync));
        Assert.Equal(3, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_SCHEDULER_STATE WHERE SCHED_NAME = 'WorkerDock.Execute'"));
        var deliveries = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var delivery in deliveries) await fixture.PublishAsync(publisher, delivery);
        for (var index = 0; index < deliveries.Length; index++)
        {
            var started = await fixture.NextStartedAsync();
            Assert.Contains(started.Delivery, deliveries);
            var held = await fixture.ProbeStateAsync();
            Assert.Equal(1, held.Active);
            Assert.Equal(1, held.MaximumActive);
            if (index == 0)
            {
                var before = await fixture.ClusterCheckinsAsync();
                Assert.Equal(3, before.Count);
                await fixture.WaitForClusterCycleAsync(before);
            }
            await Task.WhenAll(workers.Select(fixture.PingAsync));
            Assert.Equal(1, (await fixture.ProbeStateAsync()).Active);
            if (index < 2) await fixture.StandbyAsync(started.Process);
            await fixture.ReleaseAsync(started);
            await fixture.NextCompletedAsync();
        }
        var final = await fixture.ProbeStateAsync();
        Assert.Equal(0, final.Active);
        Assert.Equal(1, final.MaximumActive);
        Assert.Equal(3, await fixture.DistinctWorkersAsync());
        Assert.Equal(6, await fixture.StartedRowsAsync());
        Assert.Equal(6, await fixture.EndedRowsAsync());
        Assert.Equal(6, fixture.Deliveries.Count);
        Assert.All(fixture.Deliveries.Values, count => Assert.Equal(1, count));
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].WDS_QRTZ_JOB_DETAILS"));
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
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_TRIGGERS"));
        var refused = await Assert.ThrowsAsync<WorkerDock.Application.Common.WorkerDockException>(async () => await fixture.Runner.RollbackAsync("tenant", "0002_quartz", default));
        Assert.Equal("UnsafeRollback", refused.Code);
        await fixture.StopAsync(publisher);
        var restarted = await fixture.StartAsync("publisher");
        var worker1 = await fixture.StartAsync("execution");
        var worker2 = await fixture.StartAsync("execution");
        await Task.WhenAll(fixture.ArmAsync(worker1), fixture.ArmAsync(worker2));
        var started = await fixture.NextStartedAsync();
        Assert.Equal(delivery, started.Delivery);
        await fixture.ReleaseAsync(started);
        await fixture.NextCompletedAsync();
        await fixture.StopAsync(worker1);
        await fixture.StopAsync(worker2);
        await fixture.StopAsync(restarted);
        Assert.Single(fixture.Deliveries);
        Assert.Equal(1, fixture.Deliveries[delivery]);
        Assert.Equal(1, await fixture.StartedRowsAsync());
        Assert.Equal(1, await fixture.EndedRowsAsync());
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_TRIGGERS"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Schema_round_trip_and_runtime_scheduler_starts_without_DDL(bool snapshot)
    {
        await using var fixture = new QuartzProcessFixture();
        await fixture.CreateAsync(snapshot);
        Assert.Equal(30, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock') AND name LIKE 'WD[SE][_]QRTZ[_]%'"));
        await fixture.Runner.RollbackAsync("tenant", "0002_quartz", default);
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock') AND name LIKE 'WD[SE][_]QRTZ[_]%'"));
        await fixture.Runner.ApplyAsync("tenant", "0002_quartz", default);
        await using var topology = new QuartzTopology(new(fixture.RuntimeConnectionString, fixture.Database.InstanceId));
        var options = new QuartzNodeOptions(fixture.Database.InstanceId, Guid.NewGuid().ToString("N"), 1);
        var schedule = await topology.CreateScheduleAsync(options, default);
        var execution = await topology.CreateExecutionAsync(options, default);
        await schedule.Start();
        await execution.Start();
        Assert.Equal(SchedulerStatus.Running, schedule.Status);
        Assert.Equal(SchedulerStatus.Running, execution.Status);
        await schedule.Shutdown();
        await execution.Shutdown();
    }
}
