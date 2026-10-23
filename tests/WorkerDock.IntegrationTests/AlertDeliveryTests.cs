using WorkerDock.Application.Alerts;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Infrastructure.Migrations;
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
        await database.ApplyLatestSchemaAsync();

        var sinkId = Guid.NewGuid();
        var sinkRevisionId = Guid.NewGuid();
        var newerSinkRevisionId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES('{{sinkId}}','Test',1,1);
            INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
            VALUES('{{sinkRevisionId}}','{{sinkId}}',1,N'webhook',N'{"url":"https://hooks.example.test"}',N'[]',2,60,60,5,SYSUTCDATETIME());
            INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
            VALUES('{{newerSinkRevisionId}}','{{sinkId}}',2,N'webhook',N'{"url":"https://newer.example.test"}',N'[]',2,60,60,5,SYSUTCDATETIME());
            UPDATE [workerdock].AlertSinks SET CurrentRevision=2 WHERE SinkId='{{sinkId}}';
            INSERT [workerdock].AlertRules(RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,CurrentRevision)
            VALUES('{{ruleId}}',N'Test',N'Timeout',1,60,0,NULL,'{{sinkId}}',1,1);
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc)
            VALUES('{{incidentId}}','{{ruleId}}',N'job:00000000-0000-4000-8000-000000000001',N'Timeout',N'Open',SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT [workerdock].AlertDeliveries(DeliveryId,IncidentId,SinkRevisionId,Kind,Status,AttemptOrdinal,AttemptCount,AttemptBudget,NextAvailableAtUtc,LeaseGeneration,CreatedAtUtc)
            VALUES('{{deliveryId}}','{{incidentId}}','{{sinkRevisionId}}',N'Open',N'Pending',1,0,2,SYSUTCDATETIME(),0,SYSUTCDATETIME());
            """);

        var store = new SqlAlertDeliveryStore(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var due = await store.ReadDueAsync(10, default);
        Assert.Equal(new DueAlertDelivery(deliveryId, 1), Assert.Single(due));

        var owner = Guid.NewGuid();
        var first = await store.TryBeginAsync(deliveryId, 1, owner, default) ?? throw new InvalidOperationException("Initial alert lease was not acquired.");
        Assert.Equal("https://hooks.example.test", first.Sink.Configuration.GetProperty("url").GetString());
        Assert.Null(await store.TryBeginAsync(deliveryId, 1, Guid.NewGuid(), default));
        await database.ExecuteAsync($"UPDATE [workerdock].AlertDeliveries SET LeaseExpiresAtUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE DeliveryId='{deliveryId}'");

        Assert.Null(await store.TryBeginAsync(deliveryId, 1, Guid.NewGuid(), default));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AlertDeliveryAttempts WHERE DeliveryId='{deliveryId}' AND AttemptOrdinal=1 AND Result=N'TimedOut' AND SafeCode=N'AlertLeaseExpired'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AlertDeliveries WHERE DeliveryId='{deliveryId}' AND Status=N'Pending' AND AttemptOrdinal=2 AND AttemptCount=1"));
        Assert.Empty(await store.ReadDueAsync(10, default));
        await database.ExecuteAsync($"UPDATE [workerdock].AlertDeliveries SET NextAvailableAtUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE DeliveryId='{deliveryId}'");
        Assert.Equal(new DueAlertDelivery(deliveryId, 2), Assert.Single(await store.ReadDueAsync(10, default)));

        var second = await store.TryBeginAsync(deliveryId, 2, Guid.NewGuid(), default) ?? throw new InvalidOperationException("Recovered alert lease was not acquired.");
        Assert.Equal(first.Lease.Generation + 1, second.Lease.Generation);
        Assert.True(await store.CompleteAsync(second.Lease, new AlertDeliveryResult("Accepted", "Accepted", null), default));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AlertDeliveries WHERE DeliveryId='{deliveryId}' AND Status=N'Delivered' AND AttemptCount=2"));
    }
    [Fact]
    public async Task IncidentEvaluation_CreatesDueDeliveryWithoutOutboxAndKeepsStablePinnedRevision()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();

        var sinkId = Guid.NewGuid();
        var sinkRevisionId = Guid.NewGuid();
        var newerSinkRevisionId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES('{{sinkId}}','Test',1,1);
            INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
            VALUES('{{sinkRevisionId}}','{{sinkId}}',1,N'webhook',N'{"url":"https://pinned.example.test"}',N'[]',2,1,60,5,SYSUTCDATETIME());
            INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
            VALUES('{{newerSinkRevisionId}}','{{sinkId}}',2,N'webhook',N'{"url":"https://current.example.test"}',N'[]',2,1,60,5,SYSUTCDATETIME());
            INSERT [workerdock].AlertRules(RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,CurrentRevision)
            VALUES('{{ruleId}}',N'Worker down',N'WorkerUnavailable',1,60,0,NULL,'{{sinkId}}',1,1);
            INSERT [workerdock].Workers(InstallationId,BootId,CatalogDigest,CapacityCeiling,LiveLimit,LastHeartbeatUtc)
            VALUES('{{workerId}}',N'old-boot',REPLICATE('0',64),1,1,DATEADD(day,-1,SYSUTCDATETIME()));
            """);

        var evaluator = new IncidentEvaluator(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        Assert.Equal(1, await evaluator.EvaluateAsync(DateTimeOffset.UtcNow, 10, default));
        var deliveryId = await ReadDeliveryIdAsync(database);
        Assert.NotEqual(Guid.Empty, deliveryId);
        await database.ExecuteAsync($"UPDATE [workerdock].AlertSinks SET CurrentRevision=2 WHERE SinkId='{sinkId}'");
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Outbox WHERE EventType=N'AlertDispatch'"));

        var store = new SqlAlertDeliveryStore(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        Assert.Equal(new DueAlertDelivery(deliveryId, 1), Assert.Single(await store.ReadDueAsync(10, default)));
        var claim = await store.TryBeginAsync(deliveryId, 1, Guid.NewGuid(), default) ?? throw new InvalidOperationException("Created delivery was not claimable.");
        Assert.Equal("https://pinned.example.test", claim.Sink.Configuration.GetProperty("url").GetString());
        Assert.Equal(deliveryId, claim.Lease.DeliveryId);

        Assert.Equal(1, await evaluator.EvaluateAsync(DateTimeOffset.UtcNow, 10, default));
        Assert.Equal(deliveryId, await ReadDeliveryIdAsync(database));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AlertDeliveries"));
    }

    private static async Task<Guid> ReadDeliveryIdAsync(SqlDatabaseFixture database)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP(1) DeliveryId FROM [workerdock].AlertDeliveries ORDER BY CreatedAtUtc,DeliveryId";
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task LeaseLostAfterSend_DoesNotCompleteDelivery()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();

        var sinkId = Guid.NewGuid();
        var sinkRevisionId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        await database.ExecuteAsync($$"""
            INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES('{{sinkId}}','Test',1,1);
            INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc)
            VALUES('{{sinkRevisionId}}','{{sinkId}}',1,N'test',N'{}',N'[]',2,0,1,5,SYSUTCDATETIME());
            INSERT [workerdock].AlertRules(RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,CurrentRevision)
            VALUES('{{ruleId}}',N'Test',N'Timeout',1,60,0,NULL,'{{sinkId}}',1,1);
            INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc)
            VALUES('{{incidentId}}','{{ruleId}}',N'job:00000000-0000-4000-8000-000000000001',N'Timeout',N'Open',SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT [workerdock].AlertDeliveries(DeliveryId,IncidentId,SinkRevisionId,Kind,Status,AttemptOrdinal,AttemptCount,AttemptBudget,NextAvailableAtUtc,LeaseGeneration,CreatedAtUtc)
            VALUES('{{deliveryId}}','{{incidentId}}','{{sinkRevisionId}}',N'Open',N'Pending',1,0,2,SYSUTCDATETIME(),0,SYSUTCDATETIME());
            """);

        var store = new SqlAlertDeliveryStore(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId));
        var owned = new LoseRenewalStore(store);
        var sink = new SendThenWaitSink();
        var owner = Guid.NewGuid();
        var runner = new AlertDeliveryRunner(owned, [sink], new NoSecrets(), owner, TimeSpan.FromMilliseconds(5));
        await runner.RunAsync(new DueAlertDelivery(deliveryId, 1), default);

        Assert.True(sink.Sent);
        Assert.Equal("Leased", await ReadDeliveryStateAsync(database, deliveryId));
        Assert.NotEqual("Succeeded", await ReadDeliveryAttemptResultAsync(database, deliveryId));
    }

    private static async Task<string> ReadDeliveryStateAsync(SqlDatabaseFixture database, Guid deliveryId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM [workerdock].AlertDeliveries WHERE DeliveryId=@id";
        command.Parameters.AddWithValue("@id", deliveryId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ReadDeliveryAttemptResultAsync(SqlDatabaseFixture database, Guid deliveryId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP(1) Result FROM [workerdock].AlertDeliveryAttempts WHERE DeliveryId=@id ORDER BY AttemptOrdinal DESC";
        command.Parameters.AddWithValue("@id", deliveryId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed class LoseRenewalStore(IAlertDeliveryStore inner) : IAlertDeliveryStore
    {
        public ValueTask<IReadOnlyList<DueAlertDelivery>> ReadDueAsync(int limit, CancellationToken ct) => inner.ReadDueAsync(limit, ct);
        public ValueTask<AlertDeliveryClaim?> TryBeginAsync(Guid deliveryId, int expectedAttemptOrdinal, Guid ownerBootId, CancellationToken ct) => inner.TryBeginAsync(deliveryId, expectedAttemptOrdinal, ownerBootId, ct);
        public ValueTask<bool> RenewAsync(AlertDeliveryLease lease, CancellationToken ct) => ValueTask.FromResult(false);
        public ValueTask<bool> CompleteAsync(AlertDeliveryLease lease, AlertDeliveryResult result, CancellationToken ct) => inner.CompleteAsync(lease, result, ct);
        public ValueTask<WorkerDock.Application.Common.CommandReceipt> RetryFailedAsync(Guid deliveryId, int additionalAttempts, WorkerDock.Application.Common.ActorContext actor, CancellationToken ct) => inner.RetryFailedAsync(deliveryId, additionalAttempts, actor, ct);
    }

    private sealed class SendThenWaitSink : IAlertSink
    {
        public bool Sent { get; private set; }
        public string Type => "test";
        public async ValueTask<AlertDeliveryResult> DeliverAsync(System.Text.Json.JsonElement configuration, AlertEnvelope alert, WorkerDock.Contracts.Secrets.ISecretVault secrets, TimeSpan timeout, CancellationToken ct)
        {
            Sent = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return AlertDeliveryResult.Accepted();
        }
    }

    private sealed class NoSecrets : WorkerDock.Contracts.Secrets.ISecretVault
    {
        public ValueTask<WorkerDock.Contracts.Secrets.SecretValue> ResolveAsync(WorkerDock.Contracts.Secrets.SecretReference reference, CancellationToken ct) => throw new InvalidOperationException("No secret expected");
    }
}
