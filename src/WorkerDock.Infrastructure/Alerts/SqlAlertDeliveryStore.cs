using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Jobs;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class SqlAlertDeliveryStore(TenantDatabaseOptions options) : IAlertDeliveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async ValueTask<AlertDeliveryClaim?> TryBeginAsync(Guid deliveryId, int expectedAttemptOrdinal, Guid ownerBootId, CancellationToken ct)
    {
        if (deliveryId == Guid.Empty || ownerBootId == Guid.Empty || expectedAttemptOrdinal < 1)
            return null;

        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyTenantAsync(connection, transaction, token);
                // A process may die after claiming but before recording a result. Fence that
                // generation as a timed-out attempt and advance the durable attempt ordinal.
                await using (var expired = SqlAttemptStore.Command(connection, transaction, """
                    SELECT d.AttemptCount,d.AttemptBudget,d.LeaseGeneration,d.LeaseStartedAtUtc,
                           r.RetryMinSeconds,r.RetryMaxSeconds,SYSUTCDATETIME()
                    FROM [workerdock].AlertDeliveries d WITH(UPDLOCK,ROWLOCK)
                    JOIN [workerdock].AlertSinkRevisions r ON r.SinkRevisionId=d.SinkRevisionId
                    WHERE d.DeliveryId=@id AND d.AttemptOrdinal=@ordinal AND d.Status=N'Leased'
                      AND d.LeaseExpiresAtUtc<=SYSUTCDATETIME() AND d.AttemptCount=@ordinal;
                    """))
                {
                    SqlAttemptStore.Add(expired, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                    SqlAttemptStore.Add(expired, "@ordinal", SqlDbType.Int, expectedAttemptOrdinal);
                    await using var expiredReader = await expired.ExecuteReaderAsync(token);
                    if (await expiredReader.ReadAsync(token))
                    {
                        var attemptCount = expiredReader.GetInt32(0);
                        var attemptBudget = expiredReader.GetInt32(1);
                        var expiredGeneration = expiredReader.GetInt64(2);
                        var startedAt = Utc(expiredReader.GetDateTime(3));
                        var retryBase = TimeSpan.FromSeconds(expiredReader.GetInt32(4));
                        var retryMax = TimeSpan.FromSeconds(expiredReader.GetInt32(5));
                        var completedAt = Utc(expiredReader.GetDateTime(6));
                        await expiredReader.CloseAsync();

                        await using (var attempt = SqlAttemptStore.Command(connection, transaction, """
                            INSERT [workerdock].AlertDeliveryAttempts(DeliveryId,AttemptOrdinal,LeaseGeneration,Result,SafeCode,StartedAtUtc,CompletedAtUtc)
                            VALUES(@id,@ordinal,@generation,N'TimedOut',N'AlertLeaseExpired',@started,@completed);
                            """))
                        {
                            SqlAttemptStore.Add(attempt, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                            SqlAttemptStore.Add(attempt, "@ordinal", SqlDbType.Int, expectedAttemptOrdinal);
                            SqlAttemptStore.Add(attempt, "@generation", SqlDbType.BigInt, expiredGeneration);
                            SqlAttemptStore.Add(attempt, "@started", SqlDbType.DateTime2, startedAt.UtcDateTime);
                            SqlAttemptStore.Add(attempt, "@completed", SqlDbType.DateTime2, completedAt.UtcDateTime);
                            await attempt.ExecuteNonQueryAsync(token);
                        }

                        var hasBudget = attemptCount < attemptBudget;
                        var nextOrdinal = attemptCount + 1;
                        var delay = hasBudget ? RetryDelay(deliveryId, expectedAttemptOrdinal, retryBase, retryMax, null) : TimeSpan.Zero;
                        var availableAt = completedAt + delay;
                        await using (var update = SqlAttemptStore.Command(connection, transaction, """
                            UPDATE [workerdock].AlertDeliveries
                            SET Status=CASE WHEN @hasBudget=1 THEN N'Pending' ELSE N'Failed' END,
                                AttemptOrdinal=CASE WHEN @hasBudget=1 THEN @nextOrdinal ELSE AttemptOrdinal END,
                                NextAvailableAtUtc=@available,LeaseOwner=NULL,LeaseStartedAtUtc=NULL,LeaseExpiresAtUtc=NULL,
                                SafeCode=N'AlertLeaseExpired',CompletedAtUtc=CASE WHEN @hasBudget=1 THEN NULL ELSE @completed END
                            WHERE DeliveryId=@id AND Status=N'Leased' AND AttemptOrdinal=@ordinal
                              AND AttemptCount=@ordinal AND LeaseGeneration=@generation AND LeaseExpiresAtUtc<=@completed;
                            """))
                        {
                            SqlAttemptStore.Add(update, "@hasBudget", SqlDbType.Bit, hasBudget);
                            SqlAttemptStore.Add(update, "@nextOrdinal", SqlDbType.Int, nextOrdinal);
                            SqlAttemptStore.Add(update, "@available", SqlDbType.DateTime2, availableAt.UtcDateTime);
                            SqlAttemptStore.Add(update, "@completed", SqlDbType.DateTime2, completedAt.UtcDateTime);
                            SqlAttemptStore.Add(update, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                            SqlAttemptStore.Add(update, "@ordinal", SqlDbType.Int, expectedAttemptOrdinal);
                            SqlAttemptStore.Add(update, "@generation", SqlDbType.BigInt, expiredGeneration);
                            if (await update.ExecuteNonQueryAsync(token) != 1)
                                throw new WorkerDockException("AlertDeliveryOwnershipLost");
                        }

                        if (hasBudget)
                        {
                            var payload = JsonSerializer.Serialize(new { deliveryId, attemptOrdinal = nextOrdinal }, JsonOptions);
                            var digest = ProjectionEnvelope.AlertDigest(deliveryId, nextOrdinal);
                            await using var outbox = SqlAttemptStore.Command(connection, transaction, """
                                INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,
                                    SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState)
                                VALUES(@event,N'AlertDispatch',@payload,@now,@available,1,@id,@ordinal,@digest,N'Pending');
                                """);
                            SqlAttemptStore.Add(outbox, "@event", SqlDbType.UniqueIdentifier, Guid.NewGuid());
                            SqlAttemptStore.Add(outbox, "@payload", SqlDbType.NVarChar, payload, -1);
                            SqlAttemptStore.Add(outbox, "@now", SqlDbType.DateTime2, completedAt.UtcDateTime);
                            SqlAttemptStore.Add(outbox, "@available", SqlDbType.DateTime2, availableAt.UtcDateTime);
                            SqlAttemptStore.Add(outbox, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                            SqlAttemptStore.Add(outbox, "@ordinal", SqlDbType.BigInt, (long)nextOrdinal);
                            SqlAttemptStore.Add(outbox, "@digest", SqlDbType.Char, digest, 64);
                            await outbox.ExecuteNonQueryAsync(token);
                        }
                        return null;
                    }
                }

                await using var claim = SqlAttemptStore.Command(connection, transaction, """
                    DECLARE @now datetime2(7)=SYSUTCDATETIME();
                    UPDATE [workerdock].AlertDeliveries WITH(UPDLOCK,ROWLOCK)
                    SET Status=N'Leased', AttemptCount=@ordinal, LeaseOwner=@owner, LeaseStartedAtUtc=@now,
                        LeaseGeneration=LeaseGeneration+1, LeaseExpiresAtUtc=DATEADD(second,60,@now)
                    OUTPUT inserted.LeaseGeneration,inserted.LeaseExpiresAtUtc
                    WHERE DeliveryId=@id AND AttemptOrdinal=@ordinal AND Status=N'Pending'
                      AND NextAvailableAtUtc<=@now AND AttemptCount=@ordinal-1;
                    """);
                SqlAttemptStore.Add(claim, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                SqlAttemptStore.Add(claim, "@ordinal", SqlDbType.Int, expectedAttemptOrdinal);
                SqlAttemptStore.Add(claim, "@owner", SqlDbType.NVarChar, ownerBootId.ToString("N"), 128);
                await using var claimReader = await claim.ExecuteReaderAsync(token);
                if (!await claimReader.ReadAsync(token)) return null;
                var generation = claimReader.GetInt64(0);
                var expires = Utc(claimReader.GetDateTime(1));
                await claimReader.CloseAsync();

                await using var detail = SqlAttemptStore.Command(connection, transaction, """
                    SELECT d.IncidentId,d.Kind,i.Condition,i.OpenedAtUtc,
                           r.SinkId,s.Name,r.SinkType,r.ConfigurationJson,r.SecretReferencesJson,
                           r.MaxAttempts,r.RetryMinSeconds,r.RetryMaxSeconds,r.TimeoutSeconds,s.Enabled
                    FROM [workerdock].AlertDeliveries d
                    JOIN [workerdock].AlertIncidents i ON i.IncidentId=d.IncidentId
                    JOIN [workerdock].AlertSinkRevisions r ON r.SinkRevisionId=d.SinkRevisionId
                    JOIN [workerdock].AlertSinks s ON s.SinkId=r.SinkId
                    WHERE d.DeliveryId=@id AND d.Status=N'Leased' AND d.AttemptOrdinal=@ordinal
                      AND d.LeaseGeneration=@generation AND d.LeaseOwner=@owner;
                    """);
                SqlAttemptStore.Add(detail, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                SqlAttemptStore.Add(detail, "@ordinal", SqlDbType.Int, expectedAttemptOrdinal);
                SqlAttemptStore.Add(detail, "@generation", SqlDbType.BigInt, generation);
                SqlAttemptStore.Add(detail, "@owner", SqlDbType.NVarChar, ownerBootId.ToString("N"), 128);
                await using var reader = await detail.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return null;

                var incidentId = reader.GetGuid(0);
                var kind = reader.GetString(1);
                var condition = reader.GetString(2);
                var openedAt = Utc(reader.GetDateTime(3));
                var sinkId = reader.GetGuid(4);
                var sinkName = reader.GetString(5);
                var sinkType = reader.GetString(6);
                using var configurationDocument = JsonDocument.Parse(reader.GetString(7));
                using var referencesDocument = JsonDocument.Parse(reader.GetString(8));
                var maxAttempts = reader.GetInt32(9);
                var retryBaseSeconds = reader.GetInt32(10);
                var retryMaxSeconds = reader.GetInt32(11);
                var timeoutSeconds = reader.GetInt32(12);
                var enabled = reader.GetBoolean(13);
                var configuration = configurationDocument.RootElement.Clone();
                EnsureSecretReferences(configuration, referencesDocument.RootElement);

                var envelope = new AlertEnvelope(deliveryId, incidentId, kind, openedAt,
                    condition + " alert", incidentId.ToString("N"));
                var sink = new AlertSinkDefinition(sinkId, sinkName, sinkType, configuration,
                    maxAttempts, TimeSpan.FromSeconds(retryBaseSeconds), TimeSpan.FromSeconds(retryMaxSeconds),
                    TimeSpan.FromSeconds(timeoutSeconds), enabled);
                return new AlertDeliveryClaim(new(deliveryId, expectedAttemptOrdinal, generation, ownerBootId, expires), envelope, sink);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        catch (JsonException) { throw new WorkerDockException("AlertDeliveryUnavailable"); }
    }

    public async ValueTask<bool> RenewAsync(AlertDeliveryLease lease, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyTenantAsync(connection, transaction, token);
                await using var command = SqlAttemptStore.Command(connection, transaction, """
                    UPDATE [workerdock].AlertDeliveries
                    SET LeaseExpiresAtUtc=DATEADD(second,60,SYSUTCDATETIME())
                    WHERE DeliveryId=@id AND Status=N'Leased' AND AttemptOrdinal=@ordinal
                      AND LeaseGeneration=@generation AND LeaseOwner=@owner
                      AND LeaseExpiresAtUtc>SYSUTCDATETIME();
                    """);
                BindLease(command, lease);
                return await command.ExecuteNonQueryAsync(token) == 1;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<bool> CompleteAsync(AlertDeliveryLease lease, AlertDeliveryResult result, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyTenantAsync(connection, transaction, token);
                await using var select = SqlAttemptStore.Command(connection, transaction, """
                    SELECT d.AttemptCount,d.AttemptBudget,r.RetryMinSeconds,r.RetryMaxSeconds,
                           d.LeaseStartedAtUtc,SYSUTCDATETIME()
                    FROM [workerdock].AlertDeliveries d
                    JOIN [workerdock].AlertSinkRevisions r ON r.SinkRevisionId=d.SinkRevisionId
                    WHERE d.DeliveryId=@id AND d.Status=N'Leased' AND d.AttemptOrdinal=@ordinal
                      AND d.LeaseGeneration=@generation AND d.LeaseOwner=@owner
                      AND d.LeaseExpiresAtUtc>SYSUTCDATETIME();
                    """);
                BindLease(select, lease);
                SqlAttemptStore.Add(select, "@expires", SqlDbType.DateTime2, lease.ExpiresUtc.UtcDateTime);
                await using var reader = await select.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return false;
                var attemptCount = reader.GetInt32(0);
                var maxAttempts = reader.GetInt32(1);
                var retryBase = TimeSpan.FromSeconds(reader.GetInt32(2));
                var retryMax = TimeSpan.FromSeconds(reader.GetInt32(3));
                var startedAt = Utc(reader.GetDateTime(4));
                var completedAt = Utc(reader.GetDateTime(5));
                await reader.CloseAsync();

                var outcome = result.Outcome switch
                {
                    "Accepted" => "Succeeded",
                    "TransientFailure" when attemptCount < maxAttempts => "Retry",
                    "TransientFailure" => "PermanentFailure",
                    "PermanentFailure" => "PermanentFailure",
                    _ => "PermanentFailure"
                };
                var safeCode = SafeCode(result.SafeCode);
                var delay = outcome == "Retry" ? RetryDelay(lease.DeliveryId, lease.AttemptOrdinal, retryBase, retryMax, result.RetryAfter) : TimeSpan.Zero;
                var status = outcome == "Succeeded" ? "Delivered" : outcome == "Retry" ? "Pending" : "Failed";
                var availableAt = completedAt + delay;

                await using (var attempt = SqlAttemptStore.Command(connection, transaction, """
                    INSERT [workerdock].AlertDeliveryAttempts(DeliveryId,AttemptOrdinal,LeaseGeneration,Result,SafeCode,StartedAtUtc,CompletedAtUtc)
                    VALUES(@id,@ordinal,@generation,@result,@code,@started,@completed);
                    """))
                {
                    BindLease(attempt, lease);
                    SqlAttemptStore.Add(attempt, "@result", SqlDbType.NVarChar, outcome, 16);
                    SqlAttemptStore.Add(attempt, "@code", SqlDbType.NVarChar, safeCode, 80);
                    SqlAttemptStore.Add(attempt, "@started", SqlDbType.DateTime2, startedAt.UtcDateTime);
                    SqlAttemptStore.Add(attempt, "@completed", SqlDbType.DateTime2, completedAt.UtcDateTime);
                    await attempt.ExecuteNonQueryAsync(token);
                }

                await using (var update = SqlAttemptStore.Command(connection, transaction, """
                    UPDATE [workerdock].AlertDeliveries
                    SET Status=@status,AttemptOrdinal=CASE WHEN @status=N'Pending' THEN AttemptCount+1 ELSE AttemptOrdinal END,
                        NextAvailableAtUtc=@available,LeaseOwner=NULL,LeaseStartedAtUtc=NULL,LeaseExpiresAtUtc=NULL,
                        SafeCode=@code,CompletedAtUtc=CASE WHEN @status IN(N'Delivered',N'Failed') THEN @completed ELSE NULL END
                    WHERE DeliveryId=@id AND Status=N'Leased' AND AttemptOrdinal=@ordinal
                      AND LeaseGeneration=@generation AND LeaseOwner=@owner AND LeaseExpiresAtUtc>@completed;
                    """))
                {
                    BindLease(update, lease);
                    SqlAttemptStore.Add(update, "@status", SqlDbType.NVarChar, status, 16);
                    SqlAttemptStore.Add(update, "@available", SqlDbType.DateTime2, availableAt.UtcDateTime);
                    SqlAttemptStore.Add(update, "@code", SqlDbType.NVarChar, safeCode, 80);
                    SqlAttemptStore.Add(update, "@completed", SqlDbType.DateTime2, completedAt.UtcDateTime);
                    if (await update.ExecuteNonQueryAsync(token) != 1)
                        throw new WorkerDockException("AlertDeliveryOwnershipLost");
                }

                if (outcome == "Retry")
                {
                    var payload = JsonSerializer.Serialize(new { deliveryId = lease.DeliveryId, attemptOrdinal = lease.AttemptOrdinal + 1 }, JsonOptions);
                    var digest = ProjectionEnvelope.AlertDigest(lease.DeliveryId, lease.AttemptOrdinal + 1);
                    await using var outbox = SqlAttemptStore.Command(connection, transaction, """
                        INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,
                            SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState)
                        VALUES(@event,N'AlertDispatch',@payload,@now,@available,1,@id,@ordinal,@digest,N'Pending');
                        """);
                    SqlAttemptStore.Add(outbox, "@event", SqlDbType.UniqueIdentifier, Guid.NewGuid());
                    SqlAttemptStore.Add(outbox, "@payload", SqlDbType.NVarChar, payload, -1);
                    SqlAttemptStore.Add(outbox, "@now", SqlDbType.DateTime2, completedAt.UtcDateTime);
                    SqlAttemptStore.Add(outbox, "@available", SqlDbType.DateTime2, availableAt.UtcDateTime);
                    SqlAttemptStore.Add(outbox, "@id", SqlDbType.UniqueIdentifier, lease.DeliveryId);
                    SqlAttemptStore.Add(outbox, "@ordinal", SqlDbType.BigInt, lease.AttemptOrdinal + 1L);
                    SqlAttemptStore.Add(outbox, "@digest", SqlDbType.Char, digest, 64);
                    await outbox.ExecuteNonQueryAsync(token);
                }
                return true;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        catch (WorkerDockException e) when (e.Code == "AlertDeliveryOwnershipLost") { return false; }
    }

    public async ValueTask<CommandReceipt> RetryFailedAsync(Guid deliveryId, int additionalAttempts, ActorContext actor, CancellationToken ct)
    {
        if (deliveryId == Guid.Empty || additionalAttempts is < 1 or > 20) throw new WorkerDockException("ValidationFailed", "retryBudget");
        try { JobCommandValidation.ValidateActor(actor); }
        catch (ArgumentException) { throw new WorkerDockException("ValidationFailed", "actor"); }
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyTenantAsync(connection, transaction, token);
                await using var read = SqlAttemptStore.Command(connection, transaction, """
                    SELECT AttemptCount,ManualRetryCount,Status FROM [workerdock].AlertDeliveries WITH(UPDLOCK,HOLDLOCK)
                    WHERE DeliveryId=@id;
                    """);
                SqlAttemptStore.Add(read, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                await using var reader = await read.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) throw new WorkerDockException("AlertDeliveryNotFound");
                var attemptCount = reader.GetInt32(0);
                var manualRetries = reader.GetInt32(1);
                var status = reader.GetString(2);
                await reader.CloseAsync();
                if (status != "Failed") throw new WorkerDockException("AlertDeliveryNotRetryable");
                if (manualRetries >= 10 || attemptCount + additionalAttempts > 200) throw new WorkerDockException("AlertRetryBudgetExceeded");

                var nextOrdinal = attemptCount + 1;
                var newBudget = attemptCount + additionalAttempts;
                await using (var update = SqlAttemptStore.Command(connection, transaction, """
                    UPDATE [workerdock].AlertDeliveries SET Status=N'Pending',AttemptOrdinal=@ordinal,AttemptBudget=@budget,
                        ManualRetryCount=ManualRetryCount+1,NextAvailableAtUtc=SYSUTCDATETIME(),CompletedAtUtc=NULL,SafeCode=NULL
                    WHERE DeliveryId=@id AND Status=N'Failed' AND AttemptCount=@count AND ManualRetryCount=@manual;
                    """))
                {
                    SqlAttemptStore.Add(update, "@ordinal", SqlDbType.Int, nextOrdinal);
                    SqlAttemptStore.Add(update, "@budget", SqlDbType.Int, newBudget);
                    SqlAttemptStore.Add(update, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                    SqlAttemptStore.Add(update, "@count", SqlDbType.Int, attemptCount);
                    SqlAttemptStore.Add(update, "@manual", SqlDbType.Int, manualRetries);
                    if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("AlertDeliveryNotRetryable");
                }

                var payload = JsonSerializer.Serialize(new { deliveryId, attemptOrdinal = nextOrdinal }, JsonOptions);
                var digest = ProjectionEnvelope.AlertDigest(deliveryId, nextOrdinal);
                await using (var outbox = SqlAttemptStore.Command(connection, transaction, """
                    INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,
                        EntityId,EntityRevision,PayloadDigest,ProjectionState)
                    VALUES(@event,N'AlertDispatch',@payload,SYSUTCDATETIME(),SYSUTCDATETIME(),1,@id,@ordinal,@digest,N'Pending');
                    """))
                {
                    SqlAttemptStore.Add(outbox, "@event", SqlDbType.UniqueIdentifier, Guid.NewGuid());
                    SqlAttemptStore.Add(outbox, "@payload", SqlDbType.NVarChar, payload, -1);
                    SqlAttemptStore.Add(outbox, "@id", SqlDbType.UniqueIdentifier, deliveryId);
                    SqlAttemptStore.Add(outbox, "@ordinal", SqlDbType.BigInt, (long)nextOrdinal);
                    SqlAttemptStore.Add(outbox, "@digest", SqlDbType.Char, digest, 64);
                    await outbox.ExecuteNonQueryAsync(token);
                }
                return await SqlConnectionProfiles.ReceiptAsync(connection, transaction, deliveryId, "AlertDeliveryManualRetry", actor, token);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask VerifyTenantAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken ct)
    {
        await using var command = SqlAttemptStore.Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId)
            throw new WorkerDockException("TenantInstanceMismatch");
    }

    private static void BindLease(SqlCommand command, AlertDeliveryLease lease)
    {
        SqlAttemptStore.Add(command, "@id", SqlDbType.UniqueIdentifier, lease.DeliveryId);
        SqlAttemptStore.Add(command, "@ordinal", SqlDbType.Int, lease.AttemptOrdinal);
        SqlAttemptStore.Add(command, "@generation", SqlDbType.BigInt, lease.Generation);
        SqlAttemptStore.Add(command, "@owner", SqlDbType.NVarChar, lease.OwnerBootId.ToString("N"), 128);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string SafeCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && code.Length <= 80 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? code : "AlertDeliveryFailed";

    private static TimeSpan RetryDelay(Guid deliveryId, int ordinal, TimeSpan retryBase, TimeSpan retryMax, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(ordinal - 1, 0, 30);
        var multiplier = 1L << exponent;
        var ticks = retryBase.Ticks > retryMax.Ticks / multiplier ? retryMax.Ticks : retryBase.Ticks * multiplier;
        var identity = Encoding.UTF8.GetBytes(deliveryId.ToString("N") + ":" + ordinal.ToString(CultureInfo.InvariantCulture));
        var sample = BitConverter.ToUInt32(SHA256.HashData(identity), 0) / (double)uint.MaxValue;
        var jittered = (long)(ticks * (0.8 + (sample * 0.4)));
        var backoff = TimeSpan.FromTicks(Math.Clamp(jittered, 0, retryMax.Ticks));
        var requested = retryAfter.GetValueOrDefault();
        if (requested < TimeSpan.Zero) requested = TimeSpan.Zero;
        if (requested > retryMax) requested = retryMax;
        return backoff > requested ? backoff : requested;
    }

    private static void EnsureSecretReferences(JsonElement configuration, JsonElement persistedReferences)
    {
        if (persistedReferences.ValueKind != JsonValueKind.Array)
            throw new WorkerDockException("AlertDeliveryUnavailable");
        var expected = persistedReferences.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .ToArray();
        if (expected.Any(string.IsNullOrWhiteSpace) || expected.Distinct(StringComparer.Ordinal).Count() != expected.Length)
            throw new WorkerDockException("AlertDeliveryUnavailable");
        var actual = configuration.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array
            ? headers.EnumerateArray().Where(header => header.TryGetProperty("secretReference", out _))
                .Select(header => header.GetProperty("secretReference").GetString()).ToArray()
            : [];
        if (!expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new WorkerDockException("AlertDeliveryUnavailable");
    }
}
