using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Monitoring;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

public sealed class ExecutionHistorySqlTests
{
    [Trait("Store", "Sql")]
    [Fact]
    public async Task ExecutionsAsync_filters_by_bounds_uses_execution_revision_and_pages_with_filter_bound_cursor()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var now = DateTimeOffset.UtcNow;
        var jobId = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{jobId}',NULL,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{jobId}',1,N'History source job',N'workerdock.sql-server',N'1.1.0',NCHAR(123)+NCHAR(125),NULL,NCHAR(123)+NCHAR(125),'{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}');
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{jobId}',2,N'History renamed job',N'workerdock.http',N'1.1.0',NCHAR(123)+NCHAR(125),NULL,NCHAR(123)+NCHAR(125),'{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}');
            UPDATE [workerdock].Jobs SET CurrentRevision=2 WHERE JobId='{jobId}';
            """);

        var insideId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var outsideId = Guid.NewGuid();
        var exactBoundaryId = Guid.NewGuid();
        await SeedExecutionAsync(database, insideId, jobId, 1, "Succeeded", now.AddHours(-1));
        await SeedExecutionAsync(database, runningId, jobId, 1, "Running", now.AddHours(-2));
        await SeedExecutionAsync(database, outsideId, jobId, 1, "Failed", now.AddDays(-10));
        await SeedExecutionAsync(database, exactBoundaryId, jobId, 1, "Failed", now);
        var start = now.AddHours(-3);
        var end = now.AddMinutes(-30);
        await database.ExecuteAsync($"""
            INSERT [workerdock].Attempts(AttemptId,ExecutionId,AttemptNumber,State,StartedAtUtc,EndedAtUtc)
            VALUES(NEWID(),'{insideId}',1,N'Succeeded','{start.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}','{end.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}');
            """);
        var query = new SqlMonitoringQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), TimeProvider.System);
        var filter = new ExecutionHistoryFilter(now.AddDays(-7), now, null, null, null);
        var page = await query.ExecutionsAsync(filter, new(null, 1), default);

        var row = Assert.Single(page.Items);
        Assert.Equal(insideId, row.ExecutionId);
        Assert.Equal("workerdock.sql-server", row.TemplateId);
        Assert.Equal(start, row.StartedAtUtc);
        Assert.Equal(end - start, row.Duration);
        Assert.DoesNotContain(outsideId, page.Items.Select(x => x.ExecutionId));
        Assert.DoesNotContain(exactBoundaryId, page.Items.Select(x => x.ExecutionId));
        Assert.NotNull(page.NextCursor);
        var second = await query.ExecutionsAsync(filter, new(page.NextCursor, 10), default);
        var ids = page.Items.Concat(second.Items).Select(x => x.ExecutionId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(outsideId, ids);
        Assert.DoesNotContain(exactBoundaryId, ids);
        var changedFilterError = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await query.ExecutionsAsync(filter with { State = "Running" }, new(page.NextCursor, 10), default));
        Assert.Equal("ValidationFailed", changedFilterError.Code);
        Assert.Contains(runningId, second.Items.Select(x => x.ExecutionId));

        var scopedFilter = filter with { JobId = Guid.NewGuid() };
        var scoped = await query.ExecutionsAsync(scopedFilter, new(null, 10), default);
        Assert.Empty(scoped.Items);
        var firstJobFilter = filter with { JobId = jobId };
        var firstJobPage = await query.ExecutionsAsync(firstJobFilter, new(null, 1), default);
        Assert.All(firstJobPage.Items, item => Assert.Equal(jobId, item.JobId));
        var scopeMismatch = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await query.ExecutionsAsync(filter, new(firstJobPage.NextCursor, 10), default));
        Assert.Equal("ValidationFailed", scopeMismatch.Code);
    }

    private static Task SeedExecutionAsync(SqlDatabaseFixture database, Guid executionId, Guid jobId, long revision, string state, DateTimeOffset created) =>
        database.ExecuteAsync($"""
            INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc)
            VALUES('{executionId}','{jobId}',{revision},N'Manual',N'{Guid.NewGuid():N}',N'{state}',N'Known','{created.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}','{Guid.NewGuid()}','{created.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}');
            """);
}
