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
    [Fact]
    public async Task Template_and_job_limits_are_applied_on_their_owner_rows()
    {
        await using var fixture = new TenantSqlFixture(); await fixture.CreateAsync(false);
        await fixture.Runner.ApplyAsync("tenant", "0001_initial", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        var actor = new ActorContext("manager", "Anonymous", Guid.NewGuid());
        var jobs = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        var job = CommandOutcomeTests.Job();
        await jobs.SaveAsync(job, null, actor, default);
        var commands = new SqlCapacityCommands(options);

        Assert.Equal("Applied", (await commands.SetTemplateLimitAsync("workerdock.http", 3, actor, default)).State);
        Assert.Equal("Applied", (await commands.SetJobLimitAsync(job.JobId, 2, actor, default)).State);
        Assert.Equal(3, await fixture.Database.ScalarAsync("SELECT ConcurrencyLimit FROM [workerdock].TemplateCatalog WHERE TemplateId=N'workerdock.http' AND TemplateVersion=N'1.1.0'"));
        Assert.Equal(2, await fixture.Database.ScalarAsync($"SELECT ConcurrencyLimit FROM [workerdock].Jobs WHERE JobId='{job.JobId}'"));
    }

    [Fact]
    public async Task Empty_single_runtime_schema_round_trip_preserves_runtime_status()
    {
        await using var database=new SqlDatabaseFixture(); await database.CreateAsync(false);
        var runner=new MigrationRunner(database.ConnectionString,database.InstanceId,database.RuntimePrincipal,database.ScriptsRoot);
        await runner.ApplyAsync("tenant","0001_initial",default); await runner.RollbackAsync("tenant","0001_initial",default);
        await runner.ApplyAsync("tenant","0001_initial",default);
        Assert.Equal(1,await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].RuntimeStatus WHERE StateId=1 AND Ready=0 AND HostCeiling=1"));
    }
}
