using Microsoft.Data.SqlClient;
using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

public sealed class JobManagementQueryTests
{
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Detail_exposes_live_job_controls()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Controls");
        await db.ExecuteAsync($"UPDATE [workerdock].Jobs SET ConcurrencyLimit=3,PauseOnNeedsReview=1 WHERE JobId='{id}'");
        var detail = await new SqlJobManagementQueries(new(db.ConnectionString, db.InstanceId)).GetAsync(id, default);
        Assert.Equal(3, detail.Editor.ConcurrencyLimit);
        Assert.True(detail.Editor.PauseOnNeedsReview);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task History_reports_start_and_duration_from_retained_legacy_attempts()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "History", archived: true); var execution = Guid.NewGuid();
        await db.ExecuteAsync($"INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc) VALUES('{execution}','{id}',1,N'Manual',CONVERT(nvarchar(36),NEWID()),N'Succeeded',N'Known','2020-01-01',NEWID(),'2020-01-01'); INSERT [workerdock].Attempts(AttemptId,ExecutionId,AttemptNumber,State,StartedAtUtc,EndedAtUtc) VALUES(NEWID(),'{execution}',1,N'Succeeded','2020-01-01T00:00:01','2020-01-01T00:00:06')");
        var item = Assert.Single((await new SqlJobManagementQueries(new(db.ConnectionString, db.InstanceId)).ExecutionsAsync(id, new(null, 50), default)).Items);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 1, TimeSpan.Zero), item.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(5), item.Duration);
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Inventory_finds_old_drafts_and_filters_lifecycle_without_exposing_configuration()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var draft = await JobManagementTestData.SeedAsync(db, "Old draft");
        var enabled = await JobManagementTestData.SeedAsync(db, "Enabled", enabled: true);
        var archived = await JobManagementTestData.SeedAsync(db, "Archived", archived: true);
        var queries = new SqlJobManagementQueries(new(db.ConnectionString, db.InstanceId));
        Assert.Equal(2, (await queries.ListAsync(new("current", null), new(null, 50), default)).Items.Count);
        Assert.Equal(enabled, Assert.Single((await queries.ListAsync(new("enabled", null), new(null, 50), default)).Items).JobId);
        var item = Assert.Single((await queries.ListAsync(new("disabled", null), new(null, 50), default)).Items);
        Assert.Equal(draft, item.JobId);
        Assert.Null(item.ScheduleKind);
        Assert.False(item.SchedulePaused);
        Assert.True(item.ConfigurationUpdatedUtc < DateTimeOffset.UtcNow.AddDays(-90));
        Assert.Equal(archived, Assert.Single((await queries.ListAsync(new("archived", null), new(null, 50), default)).Items).JobId);
        Assert.DoesNotContain("configurationJson", JsonSerializer.Serialize(item), StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Literal_search_and_case_insensitive_name_order_produce_stable_adjacent_pages()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var names = new[] { "100%_ação", "100xxação", "Árvore", "alpha", "ALPHA", "alpha" };
        foreach (var name in names) await JobManagementTestData.SeedAsync(db, name);
        var queries = new SqlJobManagementQueries(new(db.ConnectionString, db.InstanceId));
        Assert.Equal("100%_ação", Assert.Single((await queries.ListAsync(new("current", "%_"), new(null, 50), default)).Items).Name);
        Assert.Equal("Árvore", Assert.Single((await queries.ListAsync(new("current", "Árv"), new(null, 50), default)).Items).Name);
        var ids = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await queries.ListAsync(new("current", null), new(cursor, 2), default);
            ids.AddRange(page.Items.Select(x => x.JobId)); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(names.Length, ids.Count);
        Assert.Equal(names.Length, ids.Distinct().Count());
        var first = await queries.ListAsync(new("current", null), new(null, 1), default);
        Assert.Equal("ValidationFailed", (await Assert.ThrowsAsync<WorkerDockException>(async () => await queries.ListAsync(new("archived", null), new(first.NextCursor, 1), default))).Code);
        Assert.Equal("ValidationFailed", (await Assert.ThrowsAsync<WorkerDockException>(async () => await queries.ListAsync(new("current", "x"), new(first.NextCursor, 1), default))).Code);
        Assert.Equal("ValidationFailed", (await Assert.ThrowsAsync<WorkerDockException>(async () => await queries.ListAsync(new("current", null), new("bad", 1), default))).Code);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Detail_returns_pinned_revision_version_and_legacy_removed_schedule_only_as_previous()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Archived", archived: true);
        await JobManagementTestData.ScheduleAsync(db, id, removed: true, paused: true);
        var queries = new SqlJobManagementQueries(new(db.ConnectionString, db.InstanceId));
        var detail = await queries.GetAsync(id, default);
        Assert.True(detail.Editor.Archived);
        Assert.Equal(8, detail.Editor.Version.Length);
        Assert.Equal("Archived", detail.Editor.Revision.Name);
        Assert.Null(detail.Schedule);
        Assert.Null(detail.MissedPolicy);
        Assert.NotNull(detail.PreviousRemovedSchedule);
        Assert.Equal("*/5 * * * *", detail.PreviousRemovedSchedule.Cron);
        Assert.Equal("JobNotFound", (await Assert.ThrowsAsync<WorkerDockException>(async () => await queries.GetAsync(Guid.NewGuid(), default))).Code);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task History_includes_archived_old_executions_with_stable_ties_and_empty_unknown_distinction()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Archived", archived: true);
        var emptyId = await JobManagementTestData.SeedAsync(db, "Empty");
        for (var i = 0; i < 5; i++) await db.ExecuteAsync($"INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc) VALUES(NEWID(),'{id}',1,N'Manual',CONVERT(nvarchar(36),NEWID()),N'Queued',N'Known','2020-01-01',NEWID(),'2020-01-01')");
        var queries = new SqlJobManagementQueries(new(db.ConnectionString, db.InstanceId));
        var ids = new List<Guid>(); string? cursor = null;
        do
        {
            var page = await queries.ExecutionsAsync(id, new(cursor, 2), default);
            ids.AddRange(page.Items.Select(x => x.ExecutionId)); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(5, ids.Distinct().Count());
        Assert.Empty((await queries.ExecutionsAsync(emptyId, new(null, 50), default)).Items);
        Assert.Equal("JobNotFound", (await Assert.ThrowsAsync<WorkerDockException>(async () => await queries.ExecutionsAsync(Guid.NewGuid(), new(null, 50), default))).Code);
        var first = await queries.ExecutionsAsync(id, new(null, 1), default);
        Assert.Equal("ValidationFailed", (await Assert.ThrowsAsync<WorkerDockException>(async () => await queries.ExecutionsAsync(emptyId, new(first.NextCursor, 1), default))).Code);
    }
}

internal static class JobManagementTestData
{
    public static async Task<SqlDatabaseFixture> CreateAsync()
    {
        var db = new SqlDatabaseFixture();
        await db.CreateAsync(false); await db.ApplyLatestSchemaAsync(); await JobCommandTestCatalog.RegisterSqlAsync(new(db.ConnectionString, db.InstanceId)); return db;
    }
    public static async Task<Guid> SeedAsync(SqlDatabaseFixture db, string name, bool enabled = false, bool archived = false)
    {
        var id = Guid.NewGuid();
        var policy = JsonSerializer.Serialize(new WorkerDock.Domain.Jobs.ExecutionPolicy(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 10, 5));
        await db.ExecuteAsync($"INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{id}',NULL,{(enabled ? 1 : 0)},{(archived ? 1 : 0)},1); INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc) VALUES('{id}',1,N'{name.Replace("'", "''")}',N'workerdock.http',N'1.1.0',N'{{}}',NULL,N'{policy}','2020-01-01'); UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId='{id}'");
        return id;
    }
    public static Task<int> ScheduleAsync(SqlDatabaseFixture db, Guid id, bool removed = false, bool paused = false) => db.ExecuteAsync($"INSERT [workerdock].Schedules(ScheduleId,JobId,Revision,Kind,Cron,ZoneId,ActivatedUtc,Paused,Removed,PausedAtUtc,CursorUtc,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs,ZoneRulesFingerprint) VALUES(NEWID(),'{id}',1,N'Cron',N'*/5 * * * *',N'UTC','2020-01-01',{(paused ? 1 : 0)},{(removed ? 1 : 0)},{(paused ? "'2020-01-01'" : "NULL")},'2020-01-01',N'CatchUp',120000,3,10800000,REPLICATE('A',64))");
    public static async Task<byte[]> VersionAsync(SqlDatabaseFixture db, Guid id)
    {
        await using var c = new SqlConnection(db.ConnectionString); await c.OpenAsync();
        await using var command = c.CreateCommand(); command.CommandText = "SELECT Version FROM [workerdock].Jobs WHERE JobId=@id"; command.Parameters.AddWithValue("@id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }
}
