using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

public sealed class CustomSqlSchemaTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkerDock.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root missing");
        }
    }

    [Theory]
    [InlineData("WorkerDock.Infrastructure.Persistence.TenantSettingsDbContext", "TenantSettings")]
    [InlineData("WorkerDock.Infrastructure.Jobs.JobDbContext", "Jobs")]
    [InlineData("WorkerDock.Infrastructure.Jobs.JobDbContext", "JobRevisions")]
    [InlineData("WorkerDock.Infrastructure.Jobs.JobDbContext", "Commands")]
    public void Ef_relational_model_maps_each_tenant_table_to_custom_schema(string contextName, string tableName)
    {
        var contextType = typeof(TenantSettingsService).Assembly.GetType(contextName, throwOnError: true)!;
        using var context = (DbContext)Activator.CreateInstance(contextType, "Server=unused")!;
        var table = Assert.Single(context.Model.GetRelationalModel().Tables, table => table.Name == tableName);
        Assert.Equal("workerdock", table.Schema);
    }

    [Fact]
    public void Fresh_baseline_has_one_target_and_a_reviewed_table_inventory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "tenant");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var entry = Assert.Single(manifest.RootElement.EnumerateArray());
        Assert.Equal("0001_initial", entry.GetProperty("id").GetString());
        foreach (var direction in new[] { "up", "down" })
        {
            var path = Path.Combine(directory, "0001_initial." + direction + ".sql");
            Assert.Equal(entry.GetProperty(direction + "Sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        var up = File.ReadAllText(Path.Combine(directory, "0001_initial.up.sql"));
        Assert.Contains("@InstanceId", up);
        Assert.Contains("WorkerDockRuntime", up);
        Assert.DoesNotContain("Quartz", up, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QRTZ", up, StringComparison.OrdinalIgnoreCase);
        var tables = Regex.Matches(up, @"(?im)^CREATE TABLE\s+\[workerdock\]\.\[?(?<name>\w+)\]?")
            .Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var dropped in Regex.Matches(up, @"(?im)^DROP TABLE\s+\[workerdock\]\.\[?(?<name>\w+)\]?")
            .Select(match => match.Groups["name"].Value)) tables.Remove(dropped);
        var reviewed = new[]
        {
            "AlertDeliveries", "AlertDeliveryAttempts", "AlertIncidents", "AlertRuleRevisions", "AlertRules", "AlertSinkRevisions", "AlertSinks",
            "ArchiveBatches", "ArchiveBatchItems", "Attempts", "AuditEvents", "Commands", "ConnectionProfileRevisions", "ConnectionProfiles",
            "ExecutionMetricAggregates", "Executions", "IntegrationSettings", "IntegrationSettingsRevisions", "JobRevisions", "Jobs",
            "OperationalEvents", "RetentionRequestTombstones",
            "RuntimeStatus", "ScheduleRuleScanState", "Schedules", "SchemaHistory",
            "TemplateCatalog", "TenantSettings"
        };
        Assert.Equal(reviewed.Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task FreshInstall_HasNoQuartzObjects()
    {
        await using var fixture = new TenantSqlFixture();
        await fixture.CreateAsync(false);
        var objects = await ReadStringsAsync(fixture.Database, "SELECT name FROM sys.objects WHERE schema_id=SCHEMA_ID(N'workerdock') AND is_ms_shipped=0");
        Assert.DoesNotContain(objects, name => name.Contains("QRTZ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(fixture.Database.InstanceId.ToString(), await ReadScalarAsync(fixture.Database, "SELECT CONVERT(nvarchar(36),InstanceId) FROM [workerdock].TenantSettings"));
        Assert.Equal(0, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TemplateCatalog WHERE TemplateVersion=N'1.0.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server')"));
        Assert.Equal(2, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TemplateCatalog WHERE TemplateVersion=N'1.1.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server')"));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime') AND major_id=OBJECT_ID(N'[workerdock].RuntimeStatus') AND permission_name=N'SELECT' AND state=N'G'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime') AND major_id=OBJECT_ID(N'[workerdock].RuntimeStatus') AND permission_name=N'UPDATE' AND state=N'G'"));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Fresh_baseline_rollback_is_guarded_by_business_data()
    {
        await using var fixture = new SqlDatabaseFixture();
        await fixture.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(fixture.ConnectionString, fixture.InstanceId, fixture.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        await fixture.ExecuteAsync("INSERT [workerdock].Jobs(JobId,CurrentRevision) VALUES(NEWID(),0)");
        var error = await Assert.ThrowsAsync<WorkerDock.Application.Common.WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0001_initial", default));
        Assert.Equal("UnsafeRollback", error.Code);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Jobs"));
    }

    [Theory]
    [InlineData("INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES(NEWID(),N'custom',1,1)", "SELECT COUNT(*) FROM [workerdock].AlertSinks")]
    [InlineData("INSERT [workerdock].ArchiveBatches(BatchId,RelativePath,Sha256,RowCount,EligibleThroughUtc,LogCutoffUtc,State,CreatedAtUtc) VALUES(NEWID(),N'archives/custom.ndjson',REPLICATE('A',64),1,SYSUTCDATETIME(),SYSUTCDATETIME(),N'Writing',SYSUTCDATETIME())", "SELECT COUNT(*) FROM [workerdock].ArchiveBatches")]
    [InlineData("UPDATE [workerdock].TenantSettings SET DisplayName=N'Custom tenant'", "SELECT COUNT(*) FROM [workerdock].TenantSettings WHERE DisplayName=N'Custom tenant'")]
    [InlineData("INSERT [workerdock].ConnectionProfiles(ProfileId,Name,CurrentRevisionId,Retired) VALUES(NEWID(),N'custom',NULL,0)", "SELECT COUNT(*) FROM [workerdock].ConnectionProfiles")]
    [InlineData("UPDATE [workerdock].IntegrationSettings SET ProviderId=N'custom.provider'", "SELECT COUNT(*) FROM [workerdock].IntegrationSettings WHERE ProviderId=N'custom.provider'")]
    public async Task Fresh_baseline_rollback_refuses_user_state_outside_execution_tables(string mutation, string preservedQuery)
    {
        await using var fixture = new SqlDatabaseFixture();
        await fixture.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(fixture.ConnectionString, fixture.InstanceId, fixture.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        await fixture.ExecuteAsync(mutation);

        var error = await Assert.ThrowsAsync<WorkerDock.Application.Common.WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0001_initial", default));

        Assert.Equal("UnsafeRollback", error.Code);
        Assert.Equal(1, await fixture.ScalarAsync(preservedQuery));
    }
    [Fact]
    public void Fresh_baseline_rollback_guard_covers_every_table_except_known_seeds()
    {
        var down = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "database", "tenant", "0001_initial.down.sql"));
        Assert.Contains("FROM sys.tables t JOIN sys.schemas s", down);
        Assert.True(down.IndexOf("EXEC sys.sp_executesql @GuardSql", StringComparison.Ordinal) < down.IndexOf("DROP TABLE", StringComparison.Ordinal));
        var match = Regex.Match(down, @"(?im)^\s+AND t\.name NOT IN\((?<names>[^)]*)\)");
        Assert.True(match.Success);
        var seededTables = Regex.Matches(match.Groups["names"].Value, @"N'([^']+)'")
            .Select(name => name.Groups[1].Value)
            .Order(StringComparer.Ordinal);
        var expectedSeeds = new[]
        {
            "IntegrationSettings", "IntegrationSettingsRevisions",
            "RuntimeStatus", "ScheduleRuleScanState", "SchemaHistory", "TemplateCatalog", "TenantSettings"
        }.Order(StringComparer.Ordinal);
        Assert.Equal(expectedSeeds, seededTables);
    }

    [Fact]
    public void Fresh_baseline_keeps_dedup_recovery_and_alert_cursors_on_owner_rows()
    {
        var root = Root;
        var up = File.ReadAllText(Path.Combine(root, "database", "tenant", "0001_initial.up.sql"));
        var snapshot = File.ReadAllText(Path.Combine(root, "database", "tenant", "current-state.sql"));
        foreach (var sql in new[] { up, snapshot })
        {
            Assert.DoesNotContain("ManualRequestDedup", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("ScheduleRecoveryEvents", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("AlertEvaluationState", sql, StringComparison.Ordinal);
            Assert.Contains("ManualRequestExpiresAtUtc", sql, StringComparison.Ordinal);
            Assert.Contains("UX_Executions_ManualRequest", sql, StringComparison.Ordinal);
            Assert.Contains("ManualRequestExpiresAtUtc<SYSUTCDATETIME()", sql, StringComparison.Ordinal);
            Assert.Contains("EventKind=N'ScheduleRecovery'", sql, StringComparison.Ordinal);
            Assert.Contains("LastEvaluatedAtUtc", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fresh_baseline_sql_and_runtime_queries_match_the_single_node_schema()
    {
        var root = Root;
        var up = File.ReadAllText(Path.Combine(root, "database", "tenant", "0001_initial.up.sql"));
        var snapshot = File.ReadAllText(Path.Combine(root, "database", "tenant", "current-state.sql"));
        var down = File.ReadAllText(Path.Combine(root, "database", "tenant", "0001_initial.down.sql"));
        var queue = File.ReadAllText(Path.Combine(root, "src", "WorkerDock.Infrastructure", "Executions", "SqlExecutionQueue.cs"));
        var attempts = File.ReadAllText(Path.Combine(root, "src", "WorkerDock.Infrastructure", "Executions", "SqlAttemptStore.cs"));
        var alerts = File.ReadAllText(Path.Combine(root, "src", "WorkerDock.Infrastructure", "Alerts", "IncidentEvaluator.cs"));
        var monitoring = File.ReadAllText(Path.Combine(root, "src", "WorkerDock.Infrastructure", "Monitoring", "SqlMonitoringQueries.cs"));

        Assert.Contains("AS JobConcurrencyLimit", queue);
        Assert.Contains("AS TemplateConcurrencyLimit", queue);
        Assert.DoesNotContain("candidate.EffectiveLimit", queue);
        Assert.Contains("j.Enabled,j.Archived,cat.Enabled,CONVERT(bit,0),CONVERT(uniqueidentifier,NULL)", attempts);
        Assert.Contains("MAX(r.AtUtc) DetectedAtUtc", alerts);
        Assert.DoesNotContain("MAX(r.CreatedAtUtc) DetectedAtUtc", alerts);
        Assert.Contains("COALESCE((SELECT Ready FROM [workerdock].RuntimeStatus WHERE StateId=1),CONVERT(bit,0))", monitoring);
        Assert.Contains("EXEC sys.sp_executesql N'CREATE SCHEMA [workerdock] AUTHORIZATION [dbo];'", up);
        Assert.Contains("EXEC sys.sp_executesql N'CREATE SCHEMA [workerdock] AUTHORIZATION [dbo];'", snapshot);
        Assert.Contains("CREATE PROCEDURE [workerdock].PurgeOperationalEvents", up);
        Assert.Matches(@"(?m)^GO\r?\nCREATE PROCEDURE \[workerdock\]\.PurgeOperationalEvents", up);
        Assert.Contains("i.RecordKind=N'OperationalEvent'", up);
        Assert.Contains("r.AtUtc>b.LogCutoffUtc", up);
        Assert.Contains("i.PurgedRowCount<=i.RowCount", up);
        Assert.DoesNotMatch(@"(?m)^INSERT \[workerdock\]\.OperationalEvents\(EventId,EventKind,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson\)\s*$", up);
        Assert.DoesNotContain("RetentionPurgeAudit", up, StringComparison.Ordinal);
        Assert.Contains("(i.ArchiveRemovedAtUtc IS NULL AND d.ArchiveRemovedAtUtc IS NULL OR i.ArchiveRemovedAtUtc=d.ArchiveRemovedAtUtc)", up, StringComparison.Ordinal);
        Assert.Contains("(d.ArchiveRemovedAtUtc IS NULL AND i.ArchiveRemovedAtUtc IS NULL OR d.ArchiveRemovedAtUtc=i.ArchiveRemovedAtUtc)", up, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^INSERT \[workerdock\]\.OperationalEvents\(EventId,EventKind,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson\)\s*$", snapshot);
        Assert.DoesNotContain("RetentionPurgeAudit", snapshot, StringComparison.Ordinal);
        Assert.Contains("(i.ArchiveRemovedAtUtc IS NULL AND d.ArchiveRemovedAtUtc IS NULL OR i.ArchiveRemovedAtUtc=d.ArchiveRemovedAtUtc)", snapshot, StringComparison.Ordinal);
        foreach (var obsolete in new[] { "Outbox", "ProjectionMaintenance", "Workers", "WorkerCapabilities", "ServiceHealth", "TemplatePolicies", "PolicyRevisions", "JobPermits", "AttemptStatusEvents", "ExecutionEvents" })
            Assert.DoesNotContain($"CREATE TABLE [workerdock].{obsolete}", up, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE [workerdock].RetentionPolicy", up, StringComparison.Ordinal);
        Assert.DoesNotContain("ManualRequestDedup", up, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduleRecoveryEvents", up, StringComparison.Ordinal);
        Assert.DoesNotContain("AlertEvaluationState", up, StringComparison.Ordinal);
        Assert.Contains("JOIN sys.objects o ON o.object_id=t.object_id", down);
        Assert.Contains("TemplateId NOT IN(N'workerdock.sql-server',N'workerdock.http')", down);
    }
    [Fact]
    public void Fresh_baseline_permissions_keep_audit_and_archive_protection()
    {
        var up = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "database", "tenant", "0001_initial.up.sql"));
        Assert.Contains("GRANT INSERT ON [workerdock].AuditEvents TO WorkerDockRuntime", up);
        Assert.DoesNotContain("GRANT DELETE ON [workerdock].AuditEvents TO WorkerDockRuntime", up);
        Assert.Contains("DENY DELETE ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime", up);
        Assert.Contains("PurgeAuditEvents", up);
        Assert.Contains("PurgeExpiredArchiveBatchItems", up);
    }

    private static async Task<string[]> ReadStringsAsync(SqlDatabaseFixture fixture, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>(); while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static async Task<string> ReadScalarAsync(SqlDatabaseFixture fixture, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
