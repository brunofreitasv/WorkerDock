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

    private static async Task<bool> ExistsAsync(SqlDatabaseFixture database, string sql) => await database.ScalarAsync(sql) == 1;
}
