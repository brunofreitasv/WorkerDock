using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Alerts;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class AlertHistorySqlTests
{
    [Fact]
    public async Task Incidents_keep_old_active_rows_order_by_state_and_bind_cursor_to_filter()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var sink = Guid.NewGuid(); var revision = Guid.NewGuid(); var rule = Guid.NewGuid();
        await SeedRuleAsync(database, sink, revision, rule);

        var now = DateTimeOffset.UtcNow;
        var oldOpen = Guid.NewGuid(); var unknown = Guid.NewGuid(); var resolved = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc)
            VALUES('{{oldOpen}}','{{rule}}',N'job:open',N'Timeout',N'Open','{{now.AddDays(-180).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}','{{now.AddDays(-179).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}');
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc)
            VALUES('{{unknown}}','{{rule}}',N'job:unknown',N'Timeout',N'Unknown','{{now.AddDays(-2).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}','{{now.AddDays(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}');
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc,ResolvedAtUtc)
            VALUES('{{resolved}}','{{rule}}',N'job:resolved',N'Timeout',N'Resolved','{{now.AddDays(-5).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}','{{now.AddDays(-4).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}','{{now.AddDays(-3).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}');
            """);

        var query = new SqlAlertQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var active = await query.IncidentsAsync(new(null, null, "Active", null), new(null, 1), default);
        Assert.Contains(oldOpen, active.Items.Select(x => x.IncidentId));
        Assert.NotNull(active.NextCursor);

        var all = await query.IncidentsAsync(new(null, null, "All", null), new(null, 1), default);
        var second = await query.IncidentsAsync(new(null, null, "All", null), new(all.NextCursor, 1), default);
        var third = await query.IncidentsAsync(new(null, null, "All", null), new(second.NextCursor, 1), default);
        Assert.Equal(new[] { "Open", "Unknown", "Resolved" }, all.Items.Concat(second.Items).Concat(third.Items).Select(x => x.State));
        var mismatch = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await query.IncidentsAsync(new(null, null, "Resolved", null), new(all.NextCursor, 1), default));
        Assert.Equal("ValidationFailed", mismatch.Code);
    }

    [Fact]
    public async Task Deliveries_page_independently_and_include_safe_incident_context()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var sink = Guid.NewGuid(); var revision = Guid.NewGuid(); var rule = Guid.NewGuid(); var incident = Guid.NewGuid();
        await SeedRuleAsync(database, sink, revision, rule);
        var now = DateTimeOffset.UtcNow; var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc)
            VALUES('{{incident}}','{{rule}}',N'job:safe-subject',N'Timeout',N'Open','{{now.AddDays(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}','{{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}');
            INSERT [workerdock].AlertDeliveries(DeliveryId,IncidentId,SinkRevisionId,Kind,Status,AttemptOrdinal,AttemptCount,AttemptBudget,NextAvailableAtUtc,LeaseGeneration,CreatedAtUtc,SafeCode)
            VALUES('{{first}}','{{incident}}','{{revision}}',N'Open',N'Pending',1,0,2,'{{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}',0,'{{now.AddMinutes(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}',N'WebhookUnavailable');
            INSERT [workerdock].AlertDeliveries(DeliveryId,IncidentId,SinkRevisionId,Kind,Status,AttemptOrdinal,AttemptCount,AttemptBudget,NextAvailableAtUtc,LeaseGeneration,CreatedAtUtc,CompletedAtUtc)
            VALUES('{{second}}','{{incident}}','{{revision}}',N'Reminder',N'Delivered',1,1,2,'{{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}',0,'{{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}','{{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}}');
            """);

        var query = new SqlAlertQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var filter = new DeliveryFilter(now.AddDays(-7), now.AddDays(1), null, null);
        var page = await query.DeliveriesAsync(filter, new(null, 1), default);
        var row = Assert.Single(page.Items);
        Assert.Equal("Test rule", row.RuleName);
        Assert.Equal("job:safe-subject", row.SubjectKey);
        Assert.Equal(second, row.DeliveryId);
        Assert.NotNull(page.NextCursor);
        var secondPage = await query.DeliveriesAsync(filter, new(page.NextCursor, 1), default);
        Assert.Equal(first, Assert.Single(secondPage.Items).DeliveryId);
    }

    private static Task SeedRuleAsync(SqlDatabaseFixture database, Guid sinkId, Guid revisionId, Guid ruleId) => database.ExecuteAsync($$"""
        INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES('{{sinkId}}',N'Test sink',1,1);
        INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
        VALUES('{{revisionId}}','{{sinkId}}',1,N'webhook',N'{"url":"https://hooks.example.test"}',N'[]',2,1,60,5,SYSUTCDATETIME());
        INSERT [workerdock].AlertRules(RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,CurrentRevision)
        VALUES('{{ruleId}}',N'Test rule',N'Timeout',1,60,0,NULL,'{{sinkId}}',1,1);
        """);
}
