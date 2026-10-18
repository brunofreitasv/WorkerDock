using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

public sealed class JobSqlIntegrationTests
{
    [Fact]
    public async Task Revision_audit_records_bounded_transition_and_outcome()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", "0003_execution", default);
        var commands = new SqlJobCommands(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var job = Job() with { Configuration = JsonDocument.Parse("{\"secret\":\"audit-must-not-copy-this\"}").RootElement.Clone() };
        var actor = Actor();
        await commands.SaveAsync(job, null, actor, default);
        var editor = await commands.GetEditableAsync(job.JobId, default);
        await commands.SaveAsync(job with { Name = "Second" }, editor.Version, actor, default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.AuditEvents WHERE EventType=N'JobRevisionSaved' AND JSON_VALUE(DetailsJson,'$.beforeRevision') IS NULL AND JSON_VALUE(DetailsJson,'$.afterRevision')='1' AND JSON_VALUE(DetailsJson,'$.outcome')='Applied'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.AuditEvents WHERE EventType=N'JobRevisionSaved' AND JSON_VALUE(DetailsJson,'$.beforeRevision')='1' AND JSON_VALUE(DetailsJson,'$.afterRevision')='2' AND JSON_VALUE(DetailsJson,'$.outcome')='Applied'"));
        Assert.Equal(2, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.AuditEvents WHERE EventType=N'JobRevisionSaved' AND ActorSubject=N'issuer:subject' AND AuthenticationMode=N'Entra' AND CorrelationId IS NOT NULL AND DetailsJson NOT LIKE N'%audit-must-not-copy-this%'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_rolls_back_sql_job_revision(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", "0003_execution", default);
        await database.ExecuteAsync("CREATE TRIGGER dbo.TestJobAuditFailure ON dbo.AuditEvents AFTER INSERT AS THROW 51000, 'Injected audit failure', 1;");
        var commands = new SqlJobCommands(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await commands.SaveAsync(Job(), null, Actor(), default));
        Assert.Equal("PersistenceUnavailable", error.Code);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.Jobs"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.JobRevisions"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.Commands"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_key_is_deduplicated_with_atomic_audit_and_outbox(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", "0003_execution", default);
        var commands = new SqlJobCommands(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var job = Job();
        await commands.SaveAsync(job, null, Actor(), default);
        await database.ExecuteAsync($"UPDATE dbo.Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var actor = Actor();
        var runs = await Task.WhenAll(commands.RunNowAsync(job.JobId, "same-key", actor, default).AsTask(),
            commands.RunNowAsync(job.JobId, "same-key", actor, default).AsTask());
        Assert.Equal(runs[0], runs[1]);
        await database.ExecuteAsync($"UPDATE dbo.Jobs SET Enabled=0 WHERE JobId='{job.JobId}'");
        Assert.Equal(runs[0], await commands.RunNowAsync(job.JobId, "same-key", actor, default));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.Executions"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.ManualRequestDedup"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.Outbox WHERE EventType=N'ExecutionDispatch'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.AuditEvents WHERE EventType=N'ManualRunAccepted'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.AuditEvents WHERE EventType=N'ManualRunAccepted' AND JSON_VALUE(DetailsJson,'$.beforeRevision')='1' AND JSON_VALUE(DetailsJson,'$.afterRevision')='1' AND JSON_VALUE(DetailsJson,'$.outcome')='Accepted'"));
        var unsafeRollback = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
                .RollbackAsync("tenant", "0003_execution", default));
        Assert.Equal("UnsafeRollback", unsafeRollback.Code);
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_manual_execution_and_audit()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", "0003_execution", default);
        var commands = new SqlJobCommands(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var job = Job();
        await commands.SaveAsync(job, null, Actor(), default);
        await database.ExecuteAsync($"UPDATE dbo.Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        await database.ExecuteAsync("CREATE TRIGGER dbo.TestOutboxFailure ON dbo.Outbox AFTER INSERT AS THROW 51000, 'Injected outbox failure', 1;");
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await commands.RunNowAsync(job.JobId, "request", Actor(), default));
        Assert.Equal("PersistenceUnavailable", error.Code);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.Executions"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.ManualRequestDedup"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.Outbox WHERE EventType=N'ExecutionDispatch'"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM dbo.AuditEvents WHERE EventType=N'ManualRunAccepted'"));
    }

    [Fact]
    public async Task Empty_execution_schema_can_roll_back_and_reapply()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0003_execution", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name=N'Executions'"));
        await runner.RollbackAsync("tenant", "0003_execution", default);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name=N'Executions'"));
        await runner.ApplyAsync("tenant", "0003_execution", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name=N'Executions'"));
    }

    private static ActorContext Actor() => new("issuer:subject", "Entra", Guid.NewGuid());
    private static JobRevision Job() => new(Guid.NewGuid(), 0, "SQL job", "workerdock.http", "1.0.0",
        JsonDocument.Parse("{}").RootElement.Clone(), null,
        new(null, 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 0.1, ["Transient"], false, "Queue", 10, 5));
}
