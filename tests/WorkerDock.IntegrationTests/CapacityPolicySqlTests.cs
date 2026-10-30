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
    public async Task Migration_defaults_and_live_controls_are_audited()
    {
        await using var fixture = new TenantSqlFixture(); await fixture.CreateAsync(false);
        await fixture.Runner.ApplyAsync("tenant", "0004_attempt_failure_diagnostics", default);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        var actor = new ActorContext("manager", "Anonymous", Guid.NewGuid());
        var jobs = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        var job = CommandOutcomeTests.Job();
        await jobs.SaveAsync(job, null, actor, default);
        var commands = new SqlCapacityCommands(options);

        await fixture.Runner.ApplyAsync("tenant", "0005_job_review_hold", default);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT PauseOnNeedsReview FROM [workerdock].Jobs WHERE JobId='{job.JobId}'"));
        var fresh = CommandOutcomeTests.Job();
        await jobs.SaveAsync(fresh, null, actor, default);
        Assert.Equal(0, await fixture.Database.ScalarAsync($"SELECT PauseOnNeedsReview FROM [workerdock].Jobs WHERE JobId='{fresh.JobId}'"));
        Assert.Equal("Applied", (await commands.SetJobControlsAsync(job.JobId, 3, false, actor, default)).State);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Jobs WHERE JobId='{job.JobId}' AND ConcurrencyLimit=3 AND PauseOnNeedsReview=0"));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventType=N'JobControls' AND JSON_VALUE(DetailsJson,'$.limit')='3' AND JSON_VALUE(DetailsJson,'$.pauseOnNeedsReview')='false'"));
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
