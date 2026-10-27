using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Templates.SqlServer;
namespace WorkerDock.IntegrationTests;
public sealed partial class SqlProfileTests
{
    [Trait("Store","Sql")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restricted_profile_revisions_pin_and_diagnostics_are_durable(bool snapshot)
    {
        await using var f=new TenantSqlFixture();await f.CreateAsync(snapshot);await f.Runner.ApplyAsync("tenant","0001_initial",default);
        var options=new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(f.RuntimeConnectionString,f.Database.InstanceId);
        var store=new SqlConnectionProfiles(options);var proposed=SqlTemplateTests.Profile();await store.SaveAsync(proposed,null,Actor,default);var first=await store.GetCurrentAsync(proposed.ProfileId,default);
        await store.SaveAsync(proposed with { Server="rotated" },first.Version,Actor,default);var current=await store.GetCurrentAsync(proposed.ProfileId,default);
        Assert.Equal(proposed.Server,(await store.GetRevisionAsync(first.Current.ProfileRevisionId,default)).Server);
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.SaveAsync(proposed,first.Version,Actor,default));
        var descriptor=new SqlTemplate().Descriptor;await new SqlTemplateCatalog(options,[descriptor]).RegisterAsync([descriptor],default);
        var receipt=await new SqlProfileDiagnostics(options).StartAsync(proposed.ProfileId,"Target",Actor,default);
        Assert.Equal("Pending",receipt.State);
        Assert.Equal(1,await f.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision WHERE e.ExecutionId='{receipt.TargetId}' AND e.TriggerKind=N'Diagnostic' AND e.ProfileRevisionId='{current.Current.ProfileRevisionId}' AND r.TemplateId=N'workerdock.sql-server' AND JSON_VALUE(r.PolicyJson,'$.Timeout')=N'00:00:30' AND JSON_VALUE(r.ConfigurationJson,'$.commandText')=N'SELECT 1'"));
        Assert.Equal(1,await f.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EntityId='{receipt.TargetId}' AND EventType=N'ExecutionDispatch'"));
        await store.RetireAsync(proposed.ProfileId,current.Version,Actor,default);Assert.Empty(await store.ListAsync(default));
        Assert.Equal(proposed.Server,(await store.GetRevisionAsync(first.Current.ProfileRevisionId,default)).Server);
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await new SqlProfileDiagnostics(options).StartAsync(proposed.ProfileId,"Target",Actor,default));
        await Assert.ThrowsAsync<SqlException>(async()=>await f.Database.ExecuteAsync("UPDATE [workerdock].ConnectionProfileRevisions SET ConfigurationJson=N'{}'"));
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await f.Runner.RollbackAsync("tenant","0001_initial",default));
    }
    [Trait("Store","Sql")]
    [Fact]
    public async Task Migration_round_trip_and_audit_failure_are_atomic()
    {
        await using var f=new SqlDatabaseFixture();await f.CreateAsync(false);var runner=new WorkerDock.Infrastructure.Migrations.MigrationRunner(f.ConnectionString,f.InstanceId,f.RuntimePrincipal,f.ScriptsRoot);
        await runner.ApplyAsync("tenant","0001_initial",default);await runner.RollbackAsync("tenant","0001_initial",default);await runner.ApplyAsync("tenant","0001_initial",default);
        await f.ExecuteAsync("CREATE TRIGGER [workerdock].SqlProfileAuditFailure ON [workerdock].AuditEvents AFTER INSERT AS THROW 51027,'Synthetic audit failure',1;");
        var store=new SqlConnectionProfiles(new(f.ConnectionString,f.InstanceId));
        await Assert.ThrowsAsync<WorkerDockException>(async()=>await store.SaveAsync(SqlTemplateTests.Profile(),null,Actor,default));
        Assert.Empty(await store.ListAsync(default));Assert.Equal(0,await f.ScalarAsync("SELECT COUNT(*) FROM [workerdock].ConnectionProfileRevisions"));
        await f.ExecuteAsync("DROP TRIGGER [workerdock].SqlProfileAuditFailure");
    }
}
