using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Executions;

public sealed class SqlAttemptStore(TenantDatabaseOptions options) : IAttemptStore, IAttemptSignals
{
    public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) =>
        TransactAsync(intent.ExecutionId, workerBootId, (data, now) => data.Begin(intent, workerBootId, now), ct);
    public ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct) =>
        TransactAsync(lease.ExecutionId, lease.WorkerBootId, (data, now) => data.GetDescriptor(lease, now), ct);
    public async ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct)
    {
        try { return await TransactAsync(lease.ExecutionId, null, (data, now) => data.Renew(lease, now), ct); }
        catch (WorkerDockException e) when (e.Code == "AttemptOwnershipLost") { return null; }
    }
    public async ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct)
    {
        try { await TransactAsync(lease.ExecutionId, null, (data, now) => { data.Complete(lease, completion, now); return true; }, ct); }
        catch (WorkerDockException e) when (e.Code == "AttemptOwnershipLost") { }
    }
    public ValueTask<AttemptSignal> ReadSignalAsync(AttemptLease lease, CancellationToken ct) =>
        TransactAsync(lease.ExecutionId, null, (data, now) => new AttemptSignal(data.Owned(lease, now), data.StopRequested, data.Force), ct);

    public async ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyAsync(connection, transaction, token);
                await using var command = Command(connection, transaction,
                    "SELECT TOP (@limit) ExecutionId FROM [workerdock].Attempts WHERE State=N'Running' AND LeaseExpiresUtc<=SYSUTCDATETIME() ORDER BY LeaseExpiresUtc,AttemptId");
                Add(command, "@limit", SqlDbType.Int, limit);
                var ids = new List<Guid>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) ids.Add(reader.GetGuid(0));
                var count = 0;
                foreach (var id in ids.Distinct())
                {
                    var data = await LoadAsync(connection, transaction, id, null, token);
                    var previous = data.Lease; var previousState = data.AttemptState;
                    var now = await NowAsync(connection, transaction, token);
                    if (!data.Review(now)) continue;
                    await SaveAsync(connection, transaction, data, previous, previousState, true, token); count++;
                }
                return count;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    internal async ValueTask<T> TransactAsync<T>(Guid id, string? boot, Func<AttemptLifecycle, DateTimeOffset, T> action,
        CancellationToken ct, Func<DbConnection, DbTransaction, T, CancellationToken, ValueTask>? after = null)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyAsync(connection, transaction, token);
                var data = await LoadAsync(connection, transaction, id, boot, token);
                var previous = data.Lease; var previousState = data.AttemptState;
                var now = await NowAsync(connection, transaction, token);
                var result = action(data, now);
                await SaveAsync(connection, transaction, data, previous, previousState,
                    previousState == "Running" && previous!.ExpiresUtc <= now, token);
                if (after is not null) await after(connection, transaction, result, token);
                return result;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask VerifyAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
    }
    private static async ValueTask<DateTimeOffset> NowAsync(DbConnection c, DbTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT SYSUTCDATETIME()");
        return Utc((DateTime)(await command.ExecuteScalarAsync(ct))!);
    }
    private static async ValueTask<AttemptLifecycle> LoadAsync(DbConnection c, DbTransaction t, Guid id, string? boot, CancellationToken ct)
    {
        await using var command = Command(c, t, """
            SELECT e.JobId,e.JobRevision,e.IdempotencyKey,e.State,e.Certainty,e.AvailableAtUtc,e.ProfileRevisionId,
                e.StopRequested,e.ForceStop,r.TemplateId,r.TemplateVersion,r.ConfigurationJson,r.PolicyJson,
                j.Enabled,j.Archived,cat.Enabled,CONVERT(bit,COALESCE(p.StartGateClosed,1)),o.EventId,o.PayloadJson,o.PayloadDigest,
                CASE WHEN @boot IS NULL OR EXISTS(SELECT 1 FROM [workerdock].Workers w JOIN [workerdock].WorkerCapabilities wc ON wc.InstallationId=w.InstallationId
                    JOIN [workerdock].PolicyRevisions policy ON policy.PolicyKind=N'Capacity'
                    WHERE w.BootId=@boot AND w.State=N'Ready' AND policy.State=N'Effective'
                    AND w.CatalogDigest=policy.CatalogDigest AND w.AcknowledgedRevision=policy.EffectiveRevision
                    AND p.EffectiveRevision=policy.EffectiveRevision AND p.EffectiveLimit>0
                    AND w.LiveLimit>(SELECT COUNT(*) FROM [workerdock].Attempts active WHERE active.WorkerBootId=w.BootId AND active.State=N'Running')
                    AND w.LastHeartbeatUtc>DATEADD(second,-60,SYSUTCDATETIME())
                    AND wc.TemplateId=r.TemplateId AND wc.TemplateVersion=r.TemplateVersion)
                    AND EXISTS(SELECT 1 FROM [workerdock].JobPermits permit WHERE permit.ExecutionId=e.ExecutionId) THEN 1 ELSE 0 END,COALESCE(p.EffectiveRevision,-1)
            FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                JOIN [workerdock].Jobs j ON j.JobId=e.JobId
                LEFT JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
                LEFT JOIN [workerdock].TemplatePolicies p ON p.TemplateId=r.TemplateId
                OUTER APPLY (SELECT TOP(1) EventId,PayloadJson,PayloadDigest FROM [workerdock].Outbox WHERE EntityId=e.ExecutionId AND EventType=N'ExecutionDispatch'
                    ORDER BY COALESCE(TRY_CONVERT(int,JSON_VALUE(PayloadJson,'$.attemptOrdinal')),1) DESC,COALESCE(TRY_CONVERT(bigint,JSON_VALUE(PayloadJson,'$.capacityRevision')),-1) DESC,CreatedAtUtc DESC) o
            WHERE e.ExecutionId=@id
            """);
        Add(command, "@id", SqlDbType.UniqueIdentifier, id); Add(command, "@boot", SqlDbType.NVarChar, boot, 100);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("ExecutionNotFound");
        if (reader.IsDBNull(17)) throw new WorkerDockException("DispatchIntegrityError");
        var policy = JsonSerializer.Deserialize<ExecutionPolicy>(reader.GetString(12)) ?? throw new WorkerDockException("PinnedDescriptorUnavailable");
        var descriptor = new ExecutionDescriptor(id, 1, reader.GetString(9), reader.GetString(10), reader.GetString(2),
            JsonDocument.Parse(reader.GetString(11)).RootElement.Clone(), reader.IsDBNull(6) ? null : reader.GetGuid(6), null,
            JsonSerializer.SerializeToElement(new { }), null);
        using var payload = JsonDocument.Parse(reader.GetString(18));
        var ordinal = payload.RootElement.TryGetProperty("attemptOrdinal", out var number) ? number.GetInt32() : 1;
        var capacityRevision = payload.RootElement.TryGetProperty("capacityRevision", out var epoch) ? epoch.GetInt64() : 0;
        if (reader.GetString(19) != ProjectionEnvelope.ExecutionDigest(id, reader.GetInt64(1), ordinal, capacityRevision))
            throw new WorkerDockException("DispatchIntegrityError");
        var intent = new DispatchIntent(reader.GetGuid(17), id, ordinal, descriptor.TemplateId, Utc(reader.GetDateTime(5)), policy.Priority, reader.GetInt64(1), "", capacityRevision);
        var data = new AttemptLifecycle
        {
            EffectiveCapacityRevision = reader.GetInt64(21),
            Descriptor = descriptor, Policy = policy, JobId = reader.GetGuid(0), Intent = intent with { PayloadDigest = intent.ComputeDigest() },
            State = reader.GetString(3), Uncertain = reader.GetString(4) == "Unknown", StopRequested = reader.GetBoolean(7), Force = reader.GetBoolean(8),
            GateOpen = reader.GetBoolean(13) && !reader.GetBoolean(14) && !reader.GetBoolean(16) && reader.GetInt32(20) == 1,
            DescriptorAvailable = !reader.IsDBNull(15) && reader.GetBoolean(15) && reader.IsDBNull(6)
        };
        await reader.CloseAsync();
        await using var attempt = Command(c, t, "SELECT TOP(1) AttemptId,AttemptNumber,Generation,WorkerBootId,LeaseExpiresUtc,DeadlineUtc,State,LocalExitConfirmed,SafeCode FROM [workerdock].Attempts WHERE ExecutionId=@id ORDER BY AttemptNumber DESC");
        Add(attempt, "@id", SqlDbType.UniqueIdentifier, id);
        await using var ar = await attempt.ExecuteReaderAsync(ct);
        if (await ar.ReadAsync(ct))
        {
            data.Lease = new(ar.GetGuid(0), id, ar.GetInt32(1), ar.GetInt64(2), ar.IsDBNull(3) ? "" : ar.GetString(3),
                ar.IsDBNull(4) ? DateTimeOffset.MinValue : Utc(ar.GetDateTime(4)), ar.IsDBNull(5) ? null : Utc(ar.GetDateTime(5)));
            data.AttemptState = ar.GetString(6); data.LocalExitConfirmed = ar.GetBoolean(7); data.SafeCode = ar.IsDBNull(8) ? null : ar.GetString(8);
        }
        return data;
    }

    private static async ValueTask SaveAsync(DbConnection c, DbTransaction t, AttemptLifecycle data,
        AttemptLease? previous, string? previousState, bool expired, CancellationToken ct)
    {
        if (data.Lease != previous || data.AttemptState != previousState)
        {
            var lease = data.Lease!;
            var insert = previous is null || previous.AttemptId != lease.AttemptId;
            await using var command = Command(c, t, insert ? """
                INSERT INTO [workerdock].Attempts(AttemptId,ExecutionId,AttemptNumber,WorkerBootId,Generation,State,LeaseExpiresUtc,StartedAtUtc,DeadlineUtc,LocalExitConfirmed,EffectsMayHaveOccurred)
                VALUES(@attempt,@execution,@ordinal,@boot,@generation,@state,@expires,SYSUTCDATETIME(),@deadline,@exit,@effects)
                """ : """
                UPDATE [workerdock].Attempts SET State=@state,LeaseExpiresUtc=@expires,LocalExitConfirmed=@exit,EffectsMayHaveOccurred=@effects,
                    EndedAtUtc=CASE WHEN @state=N'Running' THEN NULL ELSE SYSUTCDATETIME() END,SafeCode=@code
                WHERE AttemptId=@attempt AND ExecutionId=@execution AND Generation=@generation AND WorkerBootId=@boot AND State=@previousState
                    AND (@previousState<>N'Running' OR (@expired=1 AND LeaseExpiresUtc<=SYSUTCDATETIME()) OR (@expired=0 AND LeaseExpiresUtc>SYSUTCDATETIME()))
                """);
            Add(command, "@attempt", SqlDbType.UniqueIdentifier, lease.AttemptId); Add(command, "@execution", SqlDbType.UniqueIdentifier, lease.ExecutionId);
            Add(command, "@ordinal", SqlDbType.Int, lease.Ordinal); Add(command, "@boot", SqlDbType.NVarChar, lease.WorkerBootId, 100);
            Add(command, "@generation", SqlDbType.BigInt, lease.Generation); Add(command, "@state", SqlDbType.NVarChar, data.AttemptState, 30);
            Add(command, "@expires", SqlDbType.DateTime2, lease.ExpiresUtc.UtcDateTime); Add(command, "@deadline", SqlDbType.DateTime2, lease.DeadlineUtc?.UtcDateTime);
            Add(command, "@exit", SqlDbType.Bit, data.LocalExitConfirmed); Add(command, "@effects", SqlDbType.Bit, data.Uncertain);
            Add(command, "@previousState", SqlDbType.NVarChar, previousState, 30); Add(command, "@expired", SqlDbType.Bit, expired);
            Add(command, "@code", SqlDbType.NVarChar, data.SafeCode, 100);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new WorkerDockException("AttemptOwnershipLost");
        }
        if (data.Events.Count == 0)
        {
            if (data.State is "Queued" or "RetryWaiting" && !data.GateOpen)
            {
                await using var held = Command(c, t, "UPDATE [workerdock].Outbox SET ProjectionState=N'Held' WHERE EntityId=@id AND EventType=N'ExecutionDispatch' AND ProjectionState<>N'Retired'");
                Add(held, "@id", SqlDbType.UniqueIdentifier, data.Descriptor.ExecutionId); await held.ExecuteNonQueryAsync(ct);
            }
            return;
        }
        await using var update = Command(c, t, "UPDATE [workerdock].Executions SET State=@state,Certainty=@certainty,AvailableAtUtc=@due,StopRequested=@stop,ForceStop=@force WHERE ExecutionId=@id; IF @release=1 DELETE FROM [workerdock].JobPermits WHERE ExecutionId=@id;");
        Add(update, "@id", SqlDbType.UniqueIdentifier, data.Descriptor.ExecutionId); Add(update, "@state", SqlDbType.NVarChar, data.State, 30);
        Add(update, "@certainty", SqlDbType.NVarChar, data.Uncertain ? "Unknown" : "Known", 10); Add(update, "@due", SqlDbType.DateTime2, data.Intent.DueUtc.UtcDateTime);
        Add(update, "@stop", SqlDbType.Bit, data.StopRequested); Add(update, "@force", SqlDbType.Bit, data.Force); Add(update, "@release", SqlDbType.Bit, !data.PermitRetained);
        await update.ExecuteNonQueryAsync(ct);
        foreach (var state in data.Events)
        {
            await using var ev = Command(c, t, "INSERT INTO [workerdock].AttemptStatusEvents(EventId,ExecutionId,AttemptId,State,OccurredAtUtc) VALUES(NEWID(),@id,@attempt,@state,SYSUTCDATETIME())");
            Add(ev, "@id", SqlDbType.UniqueIdentifier, data.Descriptor.ExecutionId); Add(ev, "@attempt", SqlDbType.UniqueIdentifier, data.Lease?.AttemptId); Add(ev, "@state", SqlDbType.NVarChar, state, 40);
            await ev.ExecuteNonQueryAsync(ct);
        }
        if (!data.PermitRetained)
            await WorkerDock.Infrastructure.Dispatch.JobAdmission.AdmitAsync(c, t, data.JobId, ct);
        if (data.NewDispatch is { } intent)
        {
            await using var outbox = Command(c, t, "INSERT INTO [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState) VALUES(@id,N'ExecutionDispatch',@payload,SYSUTCDATETIME(),@due,1,@execution,@revision,@digest,N'Pending')");
            Add(outbox, "@id", SqlDbType.UniqueIdentifier, intent.DeliveryId); Add(outbox, "@execution", SqlDbType.UniqueIdentifier, intent.ExecutionId);
            Add(outbox, "@revision", SqlDbType.BigInt, intent.EntityRevision); Add(outbox, "@due", SqlDbType.DateTime2, intent.DueUtc.UtcDateTime);
            Add(outbox, "@digest", SqlDbType.Char, ProjectionEnvelope.ExecutionDigest(intent.ExecutionId, intent.EntityRevision, intent.AttemptOrdinal), 64);
            Add(outbox, "@payload", SqlDbType.NVarChar, JsonSerializer.Serialize(new { executionId = intent.ExecutionId, jobRevision = intent.EntityRevision, attemptOrdinal = intent.AttemptOrdinal }), -1);
            await outbox.ExecuteNonQueryAsync(ct);
        }
    }
    internal static SqlCommand Command(DbConnection c, DbTransaction t, string sql) => new(sql, (SqlConnection)c, (SqlTransaction)t) { CommandTimeout = 10 };
    internal static void Add(SqlCommand command, string name, SqlDbType type, object? value, int size = 0) =>
        command.Parameters.Add(new SqlParameter(name, type, size) { Value = value ?? DBNull.Value });
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
