using WorkerDock.Application.Alerts;
using WorkerDock.Contracts.Alerts;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Infrastructure.Alerts;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class AlertDeliveryTests
{
    [Fact]
    public async Task Expired_lease_is_recorded_and_recovered_as_a_new_bounded_attempt()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", "0007_alerts", default);

        var sinkId = Guid.NewGuid();
        var sinkRevisionId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES('{{sinkId}}','Test',1,1);
            INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
            VALUES('{{sinkRevisionId}}','{{sinkId}}',1,N'webhook',N'{"url":"https://hooks.example.test"}',N'[]',2,0,1,5,SYSUTCDATETIME());
            INSERT [workerdock].AlertRules(RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,CurrentRevision)
            VALUES('{{ruleId}}',N'Test',N'Timeout',1,60,0,NULL,'{{sinkId}}',1,1);
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc)
            VALUES('{{incidentId}}','{{ruleId}}',N'job:00000000-0000-4000-8000-000000000001',N'Timeout',N'Open',SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT [workerdock].AlertDeliveries(DeliveryId,IncidentId,SinkRevisionId,Kind,Status,AttemptOrdinal,AttemptCount,AttemptBudget,NextAvailableAtUtc,LeaseGeneration,CreatedAtUtc)
            VALUES('{{deliveryId}}','{{incidentId}}','{{sinkRevisionId}}',N'Open',N'Pending',1,0,2,SYSUTCDATETIME(),0,SYSUTCDATETIME());
            """);

        var store = new SqlAlertDeliveryStore(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var owner = Guid.NewGuid();
        var first = await store.TryBeginAsync(deliveryId, 1, owner, default) ?? throw new InvalidOperationException("Initial alert lease was not acquired.");
        await database.ExecuteAsync($"UPDATE [workerdock].AlertDeliveries SET LeaseExpiresAtUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE DeliveryId='{deliveryId}'");

        Assert.Null(await store.TryBeginAsync(deliveryId, 1, Guid.NewGuid(), default));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AlertDeliveryAttempts WHERE DeliveryId='{deliveryId}' AND AttemptOrdinal=1 AND Result=N'TimedOut' AND SafeCode=N'AlertLeaseExpired'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AlertDeliveries WHERE DeliveryId='{deliveryId}' AND Status=N'Pending' AND AttemptOrdinal=2 AND AttemptCount=1"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EventType=N'AlertDispatch' AND EntityId='{deliveryId}' AND EntityRevision=2"));

        var second = await store.TryBeginAsync(deliveryId, 2, Guid.NewGuid(), default) ?? throw new InvalidOperationException("Recovered alert lease was not acquired.");
        Assert.Equal(first.Lease.Generation + 1, second.Lease.Generation);
        Assert.True(await store.CompleteAsync(second.Lease, new AlertDeliveryResult("Accepted", "Accepted", null), default));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AlertDeliveries WHERE DeliveryId='{deliveryId}' AND Status=N'Delivered' AND AttemptCount=2"));
    }
}
