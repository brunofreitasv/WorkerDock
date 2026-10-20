using System.Text.Json;
using System.IO.Compression;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Retention;
using WorkerDock.Quartz.Scheduling;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class DynamicValuePersistenceTests
{
    [Fact]
    public async Task Archive_json_retains_value_context_and_snapshot_without_secrets()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var contextJson = "{\"resolverVersion\":1,\"timeZoneId\":\"UTC\",\"triggerKind\":\"Manual\",\"scheduledForUtc\":null,\"requestedAtUtc\":\"2026-10-21T00:00:00Z\",\"referenceDateOverride\":\"2026-10-19\",\"firstStartedAtUtc\":null}";
        var snapshotJson = "{\"resolverVersion\":1,\"inputDigest\":\"" + new string('A', 64) + "\",\"values\":{\"/referenceDate\":\"2026-10-19\"}}";
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{{jobId}}',NULL,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{{jobId}}',1,N'archive',N'workerdock.http',N'1.0.0',N'{}',NULL,N'{}',SYSUTCDATETIME());
            UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId='{{jobId}}';
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc,AggregatedAtUtc,ValueContextJson,ValueSnapshotJson)
            VALUES('{{executionId}}','{{jobId}}',1,N'Manual',N'archive-key',N'Succeeded',N'Known',SYSUTCDATETIME(),NEWID(),DATEADD(day,-30,SYSUTCDATETIME()),DATEADD(day,-29,SYSUTCDATETIME()),N'{{contextJson}}',N'{{snapshotJson}}');
            INSERT [workerdock].ArchiveBatches(BatchId,RelativePath,Sha256,RowCount,EligibleThroughUtc,LogCutoffUtc,State,CreatedAtUtc)
            VALUES('{{batchId}}',N'{{batchId:N}}.archive.gz',REPLICATE('0',64),1,SYSUTCDATETIME(),DATEADD(day,-365,SYSUTCDATETIME()),N'Writing',DATEADD(minute,-1,SYSUTCDATETIME()));
            INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId) VALUES('{{batchId}}',N'Execution','{{executionId}}');
            UPDATE [workerdock].RetentionPolicy SET ArchiveBeforeDelete=1,ArchiveDays=90,ExecutionDays=1 WHERE PolicyId=1;
            """);
        var archiveRoot = Path.Combine(Path.GetTempPath(), "workerdock-task6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveRoot);
        try
        {
            var service = new SqlRetentionService(new(database.ConnectionString, database.InstanceId), archiveRoot);
            await service.RunBatchAsync(now, 100, default);
            await using var file = File.OpenRead(Path.Combine(archiveRoot, batchId.ToString("N") + ".archive.gz"));
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var archiveLine = await reader.ReadToEndAsync();
            using var archive = JsonDocument.Parse(archiveLine);
            var record = archive.RootElement.GetProperty("record");
            using var context = JsonDocument.Parse(record.GetProperty("ValueContextJson").GetString()!);
            using var snapshot = JsonDocument.Parse(record.GetProperty("ValueSnapshotJson").GetString()!);
            Assert.Equal("2026-10-19", context.RootElement.GetProperty("referenceDateOverride").GetString());
            Assert.Equal("2026-10-19", snapshot.RootElement.GetProperty("values").GetProperty("/referenceDate").GetString());
            Assert.DoesNotContain("secret", archiveLine, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(archiveRoot, true); }
    }

    [Fact]
    public async Task Migration_keeps_legacy_rows_null()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0008_retention", default);
        var jobId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{{jobId}}',NULL,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{{jobId}}',1,N'legacy',N'workerdock.http',N'1.0.0',N'{}',NULL,N'{}',SYSUTCDATETIME());
            UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId='{{jobId}}';
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
            VALUES('{{executionId}}','{{jobId}}',1,N'Manual',N'legacy-key',N'Queued',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME());
            """);
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].JobRevisions WHERE JobId='{jobId}' AND DynamicValuesJson IS NULL"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{executionId}' AND ValueContextJson IS NULL AND FirstStartedAtUtc IS NULL AND ValueSnapshotJson IS NULL AND ValueResolutionError IS NULL"));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("context")]
    [InlineData("snapshot")]
    public async Task Rollback_refuses_populated_dynamic_state(string stateKind)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var plan = stateKind == "plan" ? "N'{\"resolverVersion\":1,\"timeZoneId\":\"UTC\",\"bindings\":[]}'" : "NULL";
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{{jobId}}',NULL,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc,DynamicValuesJson)
            VALUES('{{jobId}}',1,N'plan',N'workerdock.http',N'1.0.0',N'{}',NULL,N'{}',SYSUTCDATETIME(),{{plan}});
            UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId='{{jobId}}';
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
            VALUES('{{executionId}}','{{jobId}}',1,N'Manual',N'key',N'Queued',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME());
            """);
        if (stateKind == "context")
            await database.ExecuteAsync($"UPDATE [workerdock].Executions SET ValueContextJson=N'{{\"resolverVersion\":1,\"timeZoneId\":\"UTC\",\"triggerKind\":\"Manual\",\"scheduledForUtc\":null,\"requestedAtUtc\":\"2026-10-20T00:00:00Z\",\"referenceDateOverride\":null,\"firstStartedAtUtc\":null}}' WHERE ExecutionId='{executionId}'");
        if (stateKind == "snapshot")
            await database.ExecuteAsync($"UPDATE [workerdock].Executions SET ValueSnapshotJson=N'{{\"resolverVersion\":1,\"inputDigest\":\"{new string('A', 64)}\",\"values\":{{}}}}' WHERE ExecutionId='{executionId}'");
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0009_dynamic_values", default));
        Assert.Equal("UnsafeRollback", error.Code);
    }

    [Fact]
    public async Task Save_load_duplicate_freeze_dynamic_plan()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var catalog = JobCommandTestCatalog.Create();
        var commands = new SqlJobCommands(options, catalog);
        var actor = new ActorContext("issuer:subject", "Entra", Guid.NewGuid());
        var sidecar = JsonDocument.Parse("""{"resolverVersion":1,"timeZoneId":"America/Sao_Paulo","bindings":[]}""");
        using var expectedPlan = JsonDocument.Parse("""{"resolverVersion":1,"timeZoneId":"America/Sao_Paulo","bindings":[]}""");
        var job = new JobRevision(Guid.NewGuid(), 0, "Dynamic", "workerdock.http", "1.0.0",
            JsonDocument.Parse("{}").RootElement.Clone(), null,
            new(null, 1, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 0.1, Array.Empty<string>(), false, "Queue", 10, 5),
            sidecar.RootElement);
        await commands.SaveAsync(job, null, actor, default);
        sidecar.Dispose();
        var loaded = await commands.GetEditableAsync(job.JobId, default);
        Assert.True(JsonElement.DeepEquals(expectedPlan.RootElement, loaded.Revision.DynamicValues!.Value));
        var lifecycle = new SqlJobLifecycle(options, catalog, new QuartzScheduleEvaluator(), TimeProvider.System);
        var duplicateId = Guid.NewGuid();
        await lifecycle.DuplicateAsync(job.JobId, duplicateId, loaded.Version, actor, default);
        var duplicate = await commands.GetEditableAsync(duplicateId, default);
        Assert.True(JsonElement.DeepEquals(loaded.Revision.DynamicValues.Value, duplicate.Revision.DynamicValues!.Value));
        var legacy = job with { JobId = Guid.NewGuid(), Name = "Legacy", DynamicValues = null };
        await commands.SaveAsync(legacy, null, actor, default);
        Assert.Null((await commands.GetEditableAsync(legacy.JobId, default)).Revision.DynamicValues);
    }
}
