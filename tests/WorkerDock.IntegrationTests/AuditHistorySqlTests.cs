using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Monitoring;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class AuditHistorySqlTests
{
    [Fact]
    public async Task AuditAsync_filters_six_month_events_by_exact_type_and_binds_cursor_to_filters()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var now = DateTimeOffset.UtcNow;
        var oldId = Guid.NewGuid();
        var oldSecondId = Guid.NewGuid();
        var prefixId = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var at = now.AddMonths(-6);
        await database.ExecuteAsync($"""
            INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
            VALUES('{oldId}',N'ManualRunAccepted',N'actor-demo',N'Session','{correlation}','{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}',NCHAR(123)+NCHAR(125));
            INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
            VALUES('{oldSecondId}',N'ManualRunAccepted',N'actor-demo',N'Session','{Guid.NewGuid()}','{at.AddMinutes(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}',NCHAR(123)+NCHAR(125));
            INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
            VALUES('{prefixId}',N'ManualRunAcceptedExtended',N'actor-demo',N'Session','{Guid.NewGuid()}','{at.AddMinutes(-2).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}',NCHAR(123)+NCHAR(125));
            INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson)
            VALUES('{Guid.NewGuid()}',N'JobRevisionSaved',N'actor-other',N'Session','{Guid.NewGuid()}','{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}',NCHAR(123)+NCHAR(125));
            """);

        var query = new SqlMonitoringQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), TimeProvider.System);
        var filter = new AuditHistoryFilter(at.AddDays(-1), now, "actor-demo", "ManualRunAccepted");
        var first = await query.AuditAsync(filter, new(null, 1), default);
        Assert.Equal(oldId, Assert.Single(first.Items).EventId);
        Assert.NotNull(first.NextCursor);
        var second = await query.AuditAsync(filter, new(first.NextCursor, 2), default);
        Assert.Equal(oldSecondId, Assert.Single(second.Items).EventId);
        Assert.DoesNotContain(prefixId, first.Items.Concat(second.Items).Select(x => x.EventId));
        Assert.DoesNotContain(Guid.Empty, first.Items.Select(x => x.EventId));

        var eventTypes = await query.AuditEventTypesAsync(default);
        Assert.Contains("ManualRunAccepted", eventTypes);
        Assert.Contains("JobRevisionSaved", eventTypes);

        var changedType = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await query.AuditAsync(filter with { EventType = "ManualRunAcceptedExtended" }, new(first.NextCursor, 10), default));
        Assert.Equal("ValidationFailed", changedType.Code);
        var tooWide = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await query.AuditAsync(filter with { FromUtc = now.AddDays(-366), ToUtc = now }, new(null, 10), default));
        Assert.Equal("ValidationFailed", tooWide.Code);
    }
}
