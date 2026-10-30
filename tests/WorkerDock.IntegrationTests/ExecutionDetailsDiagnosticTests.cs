using System.Text.Json;
using WorkerDock.Application.Monitoring;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.IntegrationTests;

public sealed class ExecutionDetailsDiagnosticTests
{
    [Fact]
    [Trait("Store", "Sql")]
    public async Task Execution_query_returns_matching_attempt_diagnostic_and_keeps_legacy_attempts()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        var commands = new WorkerDock.Infrastructure.Jobs.SqlJobCommands(options, JobCommandTestCatalog.Create());
        var actor = new WorkerDock.Application.Common.ActorContext("reader", "Anonymous", Guid.NewGuid());
        foreach (var hidden in new[] { false, true })
        {
            var job = new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(), 0, "diagnostic", "workerdock.http", "1.1.0", JsonSerializer.SerializeToElement(new { }), null, AttemptLifecycleTests.Policy(attempts: 1)) with { HideFailureDetails = hidden };
            await commands.SaveAsync(job, null, actor, default);
            await database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
            var run = await commands.RunNowAsync(job.JobId, "diagnostic", actor, default);
            var store = new WorkerDock.Infrastructure.Executions.SqlAttemptStore(options);
            var lease = (await store.BeginAsync(run.TargetId, "boot", default)).Lease!;
            var queries = new WorkerDock.Infrastructure.Monitoring.SqlMonitoringQueries(options, TimeProvider.System);
            Assert.Null(Assert.Single((await queries.ExecutionAsync(run.TargetId, default)).Attempts).Diagnostic);
            var diagnostic = new FailureDiagnostic("Http", Message: "redacted [REDACTED]", ResponseContent: "bounded body", HttpMethod: "POST", HttpStatus: 500, Truncated: true);
            var result = new TemplateResult("PermanentFailure", "HttpStatus", false, JsonSerializer.SerializeToElement(new { }), null, diagnostic);
            await store.CompleteAsync(lease, new(result, "Completed", true, false), default);
            var attempt = Assert.Single((await queries.ExecutionAsync(run.TargetId, default)).Attempts);
            Assert.Equal(lease.AttemptId, attempt.AttemptId);
            Assert.NotNull(attempt.Diagnostic);
            Assert.Equal(hidden, attempt.Diagnostic.Hidden);
            Assert.Equal(hidden ? null : diagnostic.Message, attempt.Diagnostic.Message);
            Assert.Equal(hidden ? null : diagnostic.ResponseContent, attempt.Diagnostic.ResponseContent);
            Assert.Null(attempt.Diagnostic.StackTrace);
            Assert.Equal(500, attempt.Diagnostic.HttpStatus);
            Assert.True(attempt.Diagnostic.Truncated);
        }
    }

    [Fact]
    public void Attempt_detail_serializes_diagnostic_as_camel_case_and_preserves_legacy_null()
    {
        var diagnostic = new FailureDiagnostic("Sql", Message: "redacted [REDACTED]", StackTrace: "stack", SqlNumber: 51000, SqlProcedure: "FailProc", SqlLine: 12, Truncated: true);
        var attempt = new ExecutionAttemptDto(Guid.NewGuid(), 1, "Failed", null, null, null, "CommandFailed", true, false, diagnostic);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("redacted [REDACTED]", json.RootElement.GetProperty("diagnostic").GetProperty("message").GetString());
        Assert.Equal(51000, json.RootElement.GetProperty("diagnostic").GetProperty("sqlNumber").GetInt32());
        Assert.Null((attempt with { Diagnostic = null }).Diagnostic);
    }
}
