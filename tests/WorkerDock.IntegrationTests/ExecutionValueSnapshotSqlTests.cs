using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Templates.Http;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class ExecutionValueSnapshotSqlTests
{
    [Fact]
    public async Task Crash_after_snapshot_commit_reuses_values_without_a_lifecycle_event()
    {
        await using var fixture = new TenantSqlFixture();
        await fixture.CreateAsync(false);
        await fixture.Runner.ApplyAsync("tenant", "0005_job_review_hold", default);
        var options = new TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        var template = HttpTemplateDescriptors.Get("1.1.0");
        var catalog = new TemplateCatalog([template]);
        await catalog.RegisterAsync([template], default);
        await fixture.Database.ExecuteAsync($"INSERT [workerdock].TemplateCatalog(TemplateId,TemplateVersion,MetadataDigest,Enabled) VALUES(N'workerdock.http',N'1.1.0',N'{template.MetadataDigest}',1)");
        var commands = new SqlJobCommands(options, catalog);
        var configuration = JsonSerializer.SerializeToElement(new { method = "GET", url = "https://example.test",
            query = new { }, headers = new { }, secretHeaders = new { }, parameters = new { when = "fixed" },
            successStatuses = Array.Empty<int>(), maxResponseBytes = 1024, responseMode = "SafeSummary" });
        var plan = DynamicValueJson.Write(new(1, "UTC", [new("/parameters/when", DynamicValueSource.ReferenceDate,
            null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate)]));
        var job = new JobRevision(Guid.NewGuid(), 0, "snapshot", "workerdock.http", "1.1.0",
            configuration, null, AttemptLifecycleTests.Policy(), plan);
        var actor = new ActorContext("operator", "Anonymous", Guid.NewGuid());
        await commands.SaveAsync(job, null, actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId='{job.JobId}'");
        await SqlCapacityTestSetup.ReadyAsync(options, "boot-a");
        var run = await commands.RunNowAsync(job.JobId, "snapshot-restart", actor, default);
        var first = new SqlAttemptStore(options);
        var lease = (await first.BeginAsync(run.TargetId, "boot-a", default)).Lease!;
        var priorEvents = await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].OperationalEvents WHERE ExecutionId='{run.TargetId}' AND Source=N'Lifecycle'");
        var firstDescriptor = await first.GetDescriptorAsync(lease, default);
        Assert.NotNull(firstDescriptor.ValueSnapshot);
        Assert.Equal(DateOnly.FromDateTime(firstDescriptor.ValueContext!.RequestedAtUtc!.Value.UtcDateTime).ToString("yyyy-MM-dd"),
            firstDescriptor.Configuration.GetProperty("parameters").GetProperty("when").GetString());
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{run.TargetId}' AND ValueSnapshotJson IS NOT NULL AND FirstStartedAtUtc IS NOT NULL"));
        Assert.Equal(priorEvents, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].OperationalEvents WHERE ExecutionId='{run.TargetId}' AND Source=N'Lifecycle'"));
        var restarted = new SqlAttemptStore(options);
        var secondDescriptor = await restarted.GetDescriptorAsync(lease, default);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(firstDescriptor.ValueSnapshot),
            JsonSerializer.SerializeToElement(secondDescriptor.ValueSnapshot)));
        Assert.True(JsonElement.DeepEquals(firstDescriptor.Configuration, secondDescriptor.Configuration));
        await using var connection = new SqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var read = new SqlCommand("SELECT ValueSnapshotJson FROM [workerdock].Executions WHERE ExecutionId=@id", connection);
        read.Parameters.AddWithValue("@id", run.TargetId);
        using var saved = JsonDocument.Parse((string)(await read.ExecuteScalarAsync())!);
        Assert.True(JsonElement.DeepEquals(saved.RootElement, JsonSerializer.SerializeToElement(secondDescriptor.ValueSnapshot)));
    }
}
