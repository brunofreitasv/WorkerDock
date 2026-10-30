using System.Text.Json;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Executions;

namespace WorkerDock.IntegrationTests;

public sealed class FailureDiagnosticPersistenceTests
{
    [Fact]
    public async Task Retry_keeps_first_attempt_diagnostic_when_next_attempt_succeeds()
    {
        var clock = new AttemptLifecycleTests.Clock();
        var store = new InMemoryAttemptStore(clock, AttemptLifecycleTests.Descriptor(), AttemptLifecycleTests.Policy());
        Assert.Empty(store.FailureDiagnostics);
        var first = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var result = new TemplateResult("TransientFailure", "Busy", false, JsonSerializer.SerializeToElement(new { }), null, new FailureDiagnostic("Sql", Message: "first"));
        await store.CompleteAsync(first, new(result, "Completed", true, false), default);
        var retry = Assert.Single(store.Retries);
        clock.Now = retry.DueUtc;
        var second = (await store.BeginAsync(retry, "boot", default)).Lease!;
        await store.CompleteAsync(second, new(result with { Outcome = "Success" }, "Completed", true, false), default);
        Assert.Equal(first.AttemptId, Assert.Single(store.FailureDiagnostics).Key);
        Assert.Equal("first", Assert.Single(store.FailureDiagnostics).Value.Message);
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Sql_completion_reloads_and_archives_diagnostic_then_retention_cascades_it()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        var commands = new WorkerDock.Infrastructure.Jobs.SqlJobCommands(options, JobCommandTestCatalog.Create());
        var actor = new WorkerDock.Application.Common.ActorContext("operator", "Anonymous", Guid.NewGuid());
        var job = new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(), 0, "diagnostic", "workerdock.http", "1.1.0", JsonSerializer.SerializeToElement(new { }), null, AttemptLifecycleTests.Policy(attempts: 1));
        await commands.SaveAsync(job, null, actor, default);
        await database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        var run = await commands.RunNowAsync(job.JobId, "diagnostic", actor, default);
        var store = new SqlAttemptStore(options);
        var lease = (await store.BeginAsync(run.TargetId, "boot", default)).Lease!;
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AttemptFailureDiagnostics"));
        var result = new TemplateResult("PermanentFailure", "CommandFailed", false, JsonSerializer.SerializeToElement(new { }), null, new FailureDiagnostic("Sql", Message: "persisted diagnostic"));
        await store.CompleteAsync(lease, new(result, "Completed", true, false), default);
        await new SqlAttemptStore(options).CompleteAsync(lease, new(result with { Diagnostic = new FailureDiagnostic("Sql", Message: "late") }, "Completed", true, false), default);
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AttemptFailureDiagnostics WHERE AttemptId='{lease.AttemptId}' AND JSON_VALUE(DiagnosticJson,'$.Message')=N'persisted diagnostic'"));
        await database.ExecuteAsync($"""
            UPDATE [workerdock].TenantSettings SET LogRetentionDays=1,ExecutionRetentionDays=1,AuditRetentionDays=365,ArchiveBeforeDelete=1,ArchiveRetentionDays=90,RetentionVersion=RetentionVersion+1;
            UPDATE [workerdock].Attempts SET EndedAtUtc=DATEADD(day,-10,SYSUTCDATETIME()) WHERE AttemptId='{lease.AttemptId}';
            UPDATE [workerdock].Executions SET CreatedAtUtc=DATEADD(day,-10,SYSUTCDATETIME()),AggregatedAtUtc=DATEADD(day,-9,SYSUTCDATETIME()),ManualRequestExpiresAtUtc=DATEADD(day,-8,SYSUTCDATETIME()) WHERE ExecutionId='{run.TargetId}';
            UPDATE [workerdock].OperationalEvents SET AtUtc=DATEADD(day,-9,SYSUTCDATETIME()) WHERE ExecutionId='{run.TargetId}';
            """);
        var archiveRoot = Path.Combine(Path.GetTempPath(), "workerdock-diagnostic-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new WorkerDock.Infrastructure.Retention.SqlRetentionService(options, archiveRoot).RunBatchAsync(DateTimeOffset.UtcNow, 100, default);
            var records = "";
            foreach (var path in Directory.GetFiles(archiveRoot, "*.archive.gz"))
            {
                await using var file = File.OpenRead(path);
                await using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                records += await reader.ReadToEndAsync();
            }
            Assert.Contains("persisted diagnostic", records);
            Assert.Contains("FailureDiagnostic", records);
            Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Attempts WHERE AttemptId='{lease.AttemptId}'"));
            Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AttemptFailureDiagnostics"));
        }
        finally { if (Directory.Exists(archiveRoot)) Directory.Delete(archiveRoot, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_retains_one_policy_appropriate_diagnostic_and_rejects_late_overwrite(bool hidden)
    {
        var store = new InMemoryAttemptStore(TimeProvider.System, AttemptLifecycleTests.Descriptor() with { HideFailureDetails = hidden }, AttemptLifecycleTests.Policy());
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var diagnostic = new FailureDiagnostic("Sql", Message: "message", StackTrace: "stack", ResponseContent: "body", SqlNumber: 51000);
        var result = new TemplateResult("PermanentFailure", "CommandFailed", false, JsonSerializer.SerializeToElement(new { }), null, diagnostic);
        await store.CompleteAsync(lease, new(result, "Completed", true, false), default);
        await store.CompleteAsync(lease, new(result with { Diagnostic = diagnostic with { Message = "late" } }, "Completed", true, false), default);
        var saved = Assert.Single(store.FailureDiagnostics).Value;
        Assert.Equal(lease.AttemptId, Assert.Single(store.FailureDiagnostics).Key);
        Assert.Equal(hidden ? null : "message", saved.Message);
        Assert.Equal(hidden ? null : "stack", saved.StackTrace);
        Assert.Equal(hidden ? null : "body", saved.ResponseContent);
        Assert.Equal(hidden, saved.Hidden);
        Assert.Equal(51000, saved.SqlNumber);
    }

    [Fact]
    public async Task Successful_or_invalid_diagnostics_are_not_stored()
    {
        foreach (var outcome in new[] { "Success", "PermanentFailure" })
        {
            var store = new InMemoryAttemptStore(TimeProvider.System, AttemptLifecycleTests.Descriptor(), AttemptLifecycleTests.Policy());
            var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
            var result = new TemplateResult(outcome, "Ok", false, JsonSerializer.SerializeToElement(new { }), null, new FailureDiagnostic("invalid kind"));
            await store.CompleteAsync(lease, new(result, "Completed", true, false), default);
            Assert.Empty(store.FailureDiagnostics);
        }
    }
}
