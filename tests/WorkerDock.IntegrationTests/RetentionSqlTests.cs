using WorkerDock.Application.Common;
using WorkerDock.Application.Observability;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Infrastructure.Retention;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class RetentionSqlTests
{
    [Fact]
    public async Task RetentionBatch_PreservesActiveWorkAndDedupWindow()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();

        var activeId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var requestKey = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid();
        var scopeHash = new string('A', 64);
        var keyHash = new string('B', 64);
        var requestHash = new string('C', 64);
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
            VALUES('{{activeId}}','{{jobId}}',1,N'Manual',N'active',N'Running',N'Known',DATEADD(day,-90,SYSUTCDATETIME()),NEWID(),DATEADD(day,-90,SYSUTCDATETIME()));
            INSERT [workerdock].RetentionRequestTombstones(JobId,ActorScopeHash,RequestKeyHash,RequestHash,ExecutionId,CommandId,ResponseState,AcceptedAtUtc,ExpiresAtUtc)
            VALUES('{{jobId}}','{{scopeHash}}','{{keyHash}}','{{requestHash}}','{{activeId}}','{{commandId}}',N'Accepted',DATEADD(hour,-1,SYSUTCDATETIME()),DATEADD(hour,23,SYSUTCDATETIME()));
            """);

        await database.ExecuteAsync("DECLARE @purged int; EXEC [workerdock].PurgeRetentionItems @LogCutoffUtc=SYSUTCDATETIME(),@ExecutionCutoffUtc=SYSUTCDATETIME(),@MaxRows=100,@BatchId=NULL,@PurgedRows=@purged OUTPUT;");
        await database.ExecuteAsync("DECLARE @purged int; EXEC [workerdock].PurgeRetentionRequestTombstones @CutoffUtc=SYSUTCDATETIME(),@MaxRows=100,@PurgedRows=@purged OUTPUT;");

        Assert.True(await ExistsAsync(database, $"SELECT CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Executions WHERE ExecutionId='{activeId}') THEN 1 ELSE 0 END"));
        Assert.True(await ExistsAsync(database, $"SELECT CASE WHEN EXISTS(SELECT 1 FROM [workerdock].RetentionRequestTombstones WHERE JobId='{jobId}' AND RequestKeyHash='{keyHash}') THEN 1 ELSE 0 END"));
    }

    [Fact]
    public async Task ArchivePurgeProgress_SurvivesAuditRetention()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();

        var batchId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var purgeId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            UPDATE [workerdock].TenantSettings SET LogRetentionDays=365,ExecutionRetentionDays=365,AuditRetentionDays=1,
                ArchiveBeforeDelete=0,ArchiveRetentionDays=90,RetentionVersion=RetentionVersion+1;
            UPDATE [workerdock].RetentionPolicy SET LogDays=365,ExecutionDays=365,AuditDays=1,ArchiveBeforeDelete=0,ArchiveDays=90 WHERE PolicyId=1;
            INSERT [workerdock].ArchiveBatches(BatchId,RelativePath,Sha256,RowCount,EligibleThroughUtc,LogCutoffUtc,State,CreatedAtUtc,FinalizedAtUtc)
            VALUES('{{batchId}}',N'{{batchId:N}}.archive.gz',REPLICATE('A',64),0,DATEADD(day,-100,SYSUTCDATETIME()),DATEADD(day,-365,SYSUTCDATETIME()),N'Writing',DATEADD(day,-101,SYSUTCDATETIME()),NULL);
            INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
            VALUES('{{auditId}}',N'RetentionPurge',N'WorkerDock.Retention',N'System',NEWID(),DATEADD(day,-100,SYSUTCDATETIME()),
                N'{"purgeKind":"AuditEvents","purgedRowCount":1,"archiveBatchId":"{{batchId}}"}');
            INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId) VALUES('{{batchId}}',N'AuditEvent','{{auditId}}');
            UPDATE [workerdock].ArchiveBatches SET RowCount=1,State=N'Finalized',CreatedAtUtc=DATEADD(day,-101,SYSUTCDATETIME()),
                FinalizedAtUtc=DATEADD(day,-100,SYSUTCDATETIME()),PurgedRowCount=1 WHERE BatchId='{{batchId}}';
            INSERT [workerdock].RetentionPurgeAudit(PurgeId,PurgeKind,CutoffUtc,PurgedRowCount,PurgedAtUtc,ArchiveBatchId)
            VALUES('{{purgeId}}',N'AuditEvents',DATEADD(day,-100,SYSUTCDATETIME()),1,DATEADD(day,-100,SYSUTCDATETIME()),'{{batchId}}');
            """);

        var archiveRoot = Path.Combine(Path.GetTempPath(), "workerdock-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveRoot);
        try
        {
            var service = new SqlRetentionService(new(database.ConnectionString, database.InstanceId), archiveRoot);
            await service.RunBatchAsync(DateTimeOffset.UtcNow, 100, default);

            Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventId='{auditId}'"));
            Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].ArchiveBatches WHERE BatchId='{batchId}'"));
            Assert.True(await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventType=N'RetentionPurge' AND JSON_VALUE(DetailsJson,'$.purgeKind')=N'AuditEvents'") > 0);
        }
        finally { Directory.Delete(archiveRoot, true); }
    }

    [Fact]
    public async Task ExecutionTimeline_OrdersTiedEventsAndRejectsSecretFields()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var executionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{{jobId}}',1,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{{jobId}}',1,N'timeline',N'workerdock.http',N'1.1.0',N'{}',NULL,N'{}',SYSUTCDATETIME());
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
            VALUES('{{executionId}}','{{jobId}}',1,N'Manual',N'timeline',N'Running',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME());
            """);

        var events = new SqlExecutionEvents(new(database.ConnectionString, database.InstanceId));
        var at = DateTimeOffset.UtcNow;
        var values = Enumerable.Range(1, 3).Select(sequence => new ExecutionEvent(executionId, null, "Handler", sequence, at,
            "TemplateCompleted", new Dictionary<string, string> { ["outcome"] = "Success" })).ToArray();
        await events.AppendAsync(values, default);
        var secret = "archive-secret-sentinel";
        await Assert.ThrowsAsync<WorkerDockException>(() => events.AppendAsync([
            new(executionId, null, "Handler", 4, at, "TemplateFailed", new Dictionary<string, string> { ["password"] = secret })
        ], default).AsTask());

        var queries = new SqlExecutionEventQueries(new(database.ConnectionString, database.InstanceId));
        var first = await queries.ReadAsync(executionId, null, 2, default);
        Assert.NotNull(first.NextCursor);
        var second = await queries.ReadAsync(executionId, first.NextCursor, 2, default);
        var ordered = first.Items.Concat(second.Items).ToArray();
        Assert.Equal(3, ordered.Length);
        Assert.Equal(ordered.OrderBy(item => item.AtUtc).ThenBy(item => item.EventId), ordered);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(ordered), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricAggregation_RecordsExecutionTotals()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var executionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{{jobId}}',1,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{{jobId}}',1,N'aggregate',N'workerdock.http',N'1.1.0',N'{}',NULL,N'{}',SYSUTCDATETIME());
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
            VALUES('{{executionId}}','{{jobId}}',1,N'Manual',N'aggregate',N'Succeeded',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME());
            """);

        var processed = await MetricAggregation.AggregateAsync(new(database.ConnectionString, database.InstanceId), DateTimeOffset.UtcNow.AddHours(1), 100, default);
        Assert.Equal(1, processed);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].ExecutionMetricAggregates WHERE TemplateId=N'workerdock.http' AND Outcome=N'Succeeded' AND ExecutionCount=1 AND RetryCount=0"));
    }

    [Fact]
    public void Transition_schema_consolidates_operational_state_and_keeps_archive_integrity_records()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "database", "tenant", "0013_operational_retention.up.sql"));

        Assert.Contains("OperationalEvents", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [workerdock].AlertRules", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE [workerdock].TenantSettings", sql, StringComparison.Ordinal);
        Assert.Contains("AuditEvents", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WorkerDock.Infrastructure", "Retention", "SqlRetentionService.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE [workerdock].RetentionPurgeAudit", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE [workerdock].ArchiveBatches", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE [workerdock].ArchiveBatchItems", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE [workerdock].RetentionRequestTombstones", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_progress_uses_batch_state_that_survives_audit_purge()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WorkerDock.Infrastructure", "Retention", "SqlRetentionService.cs"));
        Assert.Contains("b.PurgedRowCount", sql, StringComparison.Ordinal);
        Assert.Contains("SET PurgedRowCount=PurgedRowCount+@purged", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SUM(TRY_CONVERT(bigint,JSON_VALUE(a.DetailsJson,'$.purgedRowCount'))) FROM [workerdock].AuditEvents", sql, StringComparison.Ordinal);
    }

    private static async Task<bool> ExistsAsync(SqlDatabaseFixture database, string sql) => await database.ScalarAsync(sql) == 1;
}
