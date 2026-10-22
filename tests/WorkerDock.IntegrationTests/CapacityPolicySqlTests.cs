using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Workers;
namespace WorkerDock.IntegrationTests;

[Trait("Store","Sql")]
public sealed class CapacityPolicySqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_epoch_fences_acquired_delivery_and_preserves_exclusive_order(bool snapshot)
    {
        await using var fixture=new QuartzProcessFixture(); await fixture.CreateAsync(snapshot);
        await fixture.Runner.ApplyAsync("tenant","0009_dynamic_values",default);
        var options=new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString,fixture.Database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options,"capacity-worker");
        var jobs=new SqlJobCommands(options,JobCommandTestCatalog.Create()); var actor=new ActorContext("manager","Anonymous",Guid.NewGuid());
        var job=new JobRevision(Guid.NewGuid(),0,"capacity","workerdock.http","1.0.0",JsonSerializer.SerializeToElement(new {}),null,AttemptLifecycleTests.Policy());
        await jobs.SaveAsync(job,null,actor,default); await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var first=await jobs.RunNowAsync(job.JobId,"first",actor,default);
        var second=await jobs.RunNowAsync(job.JobId,"second",actor,default);
        Assert.Equal(1,await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].JobPermits WHERE JobId='{job.JobId}'"));
        var projections=new SqlProjectionStore(options);
        var firstEnvelope=(await projections.ReadPendingAsync(20,default)).Single(x => x.EntityId==first.TargetId);
        var acquired=(await projections.ReadAsync(firstEnvelope,1,default)).Intent;
        var commands=new SqlCapacityCommands(options); await commands.SetTemplateLimitAsync("workerdock.http",1,actor,default);
        var attempts=new SqlAttemptStore(options);
        Assert.Equal("Held",(await attempts.BeginAsync(acquired,"capacity-worker",default)).Decision);
        var capacity=new SqlCapacityStore(options);
        await capacity.TransactAsync((state,now) => { foreach(var worker in state.Workers.Values.ToArray()) state.Acknowledge(worker.InstallationId,worker.BootId,state.DesiredRevision,now); Assert.True(state.Promote(now)); return true; },default);
        Assert.Equal("Held",(await attempts.BeginAsync(acquired,"capacity-worker",default)).Decision);
        Assert.Equal(0,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Attempts"));
        await projections.MaintainAsync(default); // Bounded maintenance now runs after the publication pass.
        var current=(await projections.ReadPendingAsync(20,default)).Single(x => x.EntityId==first.TargetId);
        var fresh=(await projections.ReadAsync(current,1,default)).Intent;
        Assert.NotEqual(acquired.DeliveryId,fresh.DeliveryId); Assert.True(fresh.CapacityRevision>acquired.CapacityRevision);
        Assert.Equal("Started",(await attempts.BeginAsync(fresh,"capacity-worker",default)).Decision);
        Assert.Equal("Held",(await projections.ReadAsync((await projections.ReadPendingAsync(20,default)).Single(x => x.EntityId==second.TargetId),1,default)).State);
        Assert.Equal(1,await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Attempts WHERE State=N'Running'"));
    }
    [Fact]
    public async Task Empty_capacity_migration_round_trip_preserves_paired_schema()
    {
        await using var database=new SqlDatabaseFixture(); await database.CreateAsync(false);
        var runner=new MigrationRunner(database.ConnectionString,database.InstanceId,database.ScriptsRoot);
        await runner.ApplyAsync("tenant","0003c_capacity",default); await runner.RollbackAsync("tenant","0003c_capacity",default);
        await runner.ApplyAsync("tenant","0003c_capacity",default);
        Assert.Equal(1,await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].PolicyRevisions WHERE PolicyKind=N'Capacity' AND State=N'Pending'"));
    }
}
