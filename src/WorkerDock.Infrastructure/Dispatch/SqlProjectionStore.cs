using System.Data;
using System.Data.Common;
using WorkerDock.Infrastructure.Executions;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Dispatch;

public sealed class SqlProjectionStore(TenantDatabaseOptions options) : IProjectionStore, IDispatchStateStore, IProjectionReviewStore, IProjectionMaintenanceStore
{
    public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPendingAsync(int limit, CancellationToken ct) => ReadAsync(limit, false, ct);
    public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPublishedAsync(int limit, CancellationToken ct) => ReadAsync(limit, true, ct);

    public async ValueTask MaintainAsync(CancellationToken ct)
    {
        Guid? job = null;
        // Cursor movement commits independently: a slow/broken job cannot starve other jobs.
        await TransactAsync(async (connection, transaction, token) =>
        {
            await using var select = SqlAttemptStore.Command(connection, transaction, """
                DECLARE @after uniqueidentifier=(SELECT AfterJobId FROM [workerdock].ProjectionMaintenance WHERE Id=1);
                SELECT TOP (1) e.JobId FROM [workerdock].Executions e WITH(INDEX(IX_Executions_Maintenance))
                JOIN [workerdock].Jobs j ON j.JobId=e.JobId
                WHERE e.State IN(N'Queued',N'RetryWaiting') AND j.Enabled=1 AND j.Archived=0 AND (@after IS NULL OR e.JobId>@after)
                GROUP BY e.JobId ORDER BY e.JobId OPTION(RECOMPILE);
                """);
            job = await select.ExecuteScalarAsync(token) as Guid?;
            await using var cursor = SqlAttemptStore.Command(connection, transaction, "UPDATE [workerdock].ProjectionMaintenance SET AfterJobId=@job WHERE Id=1");
            SqlAttemptStore.Add(cursor, "@job", SqlDbType.UniqueIdentifier, job);
            await cursor.ExecuteNonQueryAsync(token);
        }, ct);
        if (job is { } id) await new JobAdmission(options).AdmitAsync(id, ct);
    }

    private async ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadAsync(int limit, bool published, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return await RunAsync<IReadOnlyList<ProjectionEnvelope>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TOP (@limit) EventId,EventType,SchemaVersion,EntityId,EntityRevision,PayloadDigest,AvailableAtUtc,PayloadJson FROM [workerdock].Outbox WHERE " +
                (published ? "DispatchedAtUtc IS NOT NULL AND ProjectionState=N'Published' AND (ReviewAtUtc IS NULL OR ReviewAtUtc<=SYSUTCDATETIME())"
                : "DispatchedAtUtc IS NULL AND ProjectionState IN (N'Pending',N'Failed') AND AvailableAtUtc<=SYSUTCDATETIME() AND (RetryAtUtc IS NULL OR RetryAtUtc<=SYSUTCDATETIME())") +
                " ORDER BY COALESCE(" + (published ? "ReviewAtUtc" : "RetryAtUtc") + ",AvailableAtUtc),CreatedAtUtc,EventId";
            command.Parameters.Add(new("@limit", SqlDbType.Int) { Value = limit });
            var result = new List<ProjectionEnvelope>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                using var payload = JsonDocument.Parse(reader.GetString(7));
                result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? Guid.Empty : reader.GetGuid(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4), reader.IsDBNull(5) ? "" : reader.GetString(5),
                    new(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)), payload.RootElement.Clone()));
            }
            return result;
        }, ct);
    }

    public async ValueTask AcknowledgeAsync(Guid intentId, string payloadDigest, CancellationToken ct)
    {
        if (payloadDigest.Length != 64 || payloadDigest.Any(c => !Uri.IsHexDigit(c))) throw new WorkerDockException("ProjectionIntegrityError");
        await TransactAsync(async (connection, transaction, token) =>
        {
            await using var command = SqlAttemptStore.Command(connection, transaction,
                "UPDATE [workerdock].Outbox SET DispatchedAtUtc=COALESCE(DispatchedAtUtc,SYSUTCDATETIME()),ProjectionState=CASE WHEN ProjectionState=N'Retired' THEN N'Retired' ELSE N'Published' END,LastErrorCode=NULL,RetryAtUtc=NULL OUTPUT inserted.EventType,inserted.EntityId WHERE EventId=@id AND PayloadDigest=@digest");
            SqlAttemptStore.Add(command, "@id", SqlDbType.UniqueIdentifier, intentId);
            SqlAttemptStore.Add(command, "@digest", SqlDbType.Char, payloadDigest, 64);
            await using var result = await command.ExecuteReaderAsync(token);
            if (!await result.ReadAsync(token)) throw new WorkerDockException("ProjectionIntegrityError");
            var eventType = result.GetString(0);
            var entityId = result.IsDBNull(1) ? Guid.Empty : result.GetGuid(1);
            await result.CloseAsync();
            if (eventType == "ExecutionDispatch") await SqlCommandOutcomes.SettleAsync(connection, transaction, entityId, token);
        }, ct);
    }

    public async ValueTask RecordFailureAsync(Guid intentId, string safeCode, DateTimeOffset retryAtUtc, CancellationToken ct)
    {
        if (safeCode is not ("UnsupportedProjection" or "ProjectionIntegrityError" or "ProjectionTriggerError" or
            "ProjectionPublicationRace" or "ProjectionHeld" or "QuartzStoreUnavailable" or "ProjectionStoreUnavailable"))
            throw new ArgumentException("Unknown projection failure code.", nameof(safeCode));
        await TransactAsync(async (connection, transaction, token) =>
        {
            await using var command = SqlAttemptStore.Command(connection, transaction,
                "UPDATE [workerdock].Outbox SET ProjectionState=CASE WHEN @code=N'ProjectionHeld' THEN N'Held' WHEN DispatchedAtUtc IS NULL THEN N'Failed' ELSE N'Published' END,LastErrorCode=@code,RetryAtUtc=@retry,ReviewAtUtc=@retry OUTPUT inserted.EventType,inserted.EntityId WHERE EventId=@id AND ProjectionState<>N'Retired'");
            SqlAttemptStore.Add(command, "@id", SqlDbType.UniqueIdentifier, intentId);
            SqlAttemptStore.Add(command, "@code", SqlDbType.NVarChar, safeCode, 40);
            SqlAttemptStore.Add(command, "@retry", SqlDbType.DateTime2, retryAtUtc.UtcDateTime);
            await using var result = await command.ExecuteReaderAsync(token);
            if (await result.ReadAsync(token))
            {
                var eventType = result.GetString(0);
                var entityId = result.IsDBNull(1) ? Guid.Empty : result.GetGuid(1);
                await result.CloseAsync();
                if (eventType == "ExecutionDispatch")
                    await SqlCommandOutcomes.SettleAsync(connection, transaction, entityId, token, safeCode is "UnsupportedProjection" or "ProjectionIntegrityError");
            }
        }, ct);
    }

    private async ValueTask TransactAsync(Func<DbConnection, DbTransaction, CancellationToken, ValueTask> action, CancellationToken ct)
    {
        try
        {
            await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await using var identity = SqlAttemptStore.Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                await action(connection, transaction, token); return true;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("ProjectionStoreUnavailable"); }
    }

    public async ValueTask CompleteReviewAsync(Guid intentId, bool retired, DateTimeOffset reviewAtUtc, CancellationToken ct)
    {
        await RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [workerdock].Outbox SET ProjectionState=CASE WHEN @retired=1 THEN N'Retired' ELSE ProjectionState END,ReviewAtUtc=@review WHERE EventId=@id AND DispatchedAtUtc IS NOT NULL";
            command.Parameters.Add(new("@id", SqlDbType.UniqueIdentifier) { Value = intentId });
            command.Parameters.Add(new("@retired", SqlDbType.Bit) { Value = retired });
            command.Parameters.Add(new("@review", SqlDbType.DateTime2) { Value = reviewAtUtc.UtcDateTime });
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public ValueTask<DispatchState> ReadAsync(ProjectionEnvelope envelope, int attemptOrdinal, CancellationToken ct) => RunAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.State,e.JobRevision,r.TemplateId,e.AvailableAtUtc,COALESCE((SELECT MAX(a.AttemptNumber) FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId),0),COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.Priority')),5) FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision WHERE e.ExecutionId=@execution";
        command.Parameters.Add(new("@execution", SqlDbType.UniqueIdentifier) { Value = envelope.EntityId });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt64(1) != envelope.EntityRevision || attemptOrdinal < 1 || attemptOrdinal > reader.GetInt32(4) + 1)
            throw new WorkerDockException("ProjectionIntegrityError");
        var intent = new DispatchIntent(envelope.IntentId, envelope.EntityId, attemptOrdinal, reader.GetString(2),
            new(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)), reader.GetInt32(5), reader.GetInt64(1), "");
        var capacityRevision = envelope.Payload.TryGetProperty("capacityRevision", out var epoch) ? epoch.GetInt64() : 0;
        intent = intent with { CapacityRevision = capacityRevision, PayloadDigest = "" };
        intent = intent with { PayloadDigest = intent.ComputeDigest() };
        var state = reader.GetString(0); var attemptExists = reader.GetInt32(4) >= attemptOrdinal;
        await reader.CloseAsync();
        await using var admission = connection.CreateCommand();
        admission.CommandText = "SELECT COUNT(*) FROM [workerdock].Executions e JOIN [workerdock].Jobs j ON j.JobId=e.JobId JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion WHERE e.ExecutionId=@id AND e.HoldsJobSlot=1 AND j.Enabled=1 AND j.Archived=0 AND cat.Enabled=1 AND cat.ConcurrencyLimit>0 AND EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus runtime WHERE runtime.StateId=1 AND runtime.Ready=1 AND runtime.LastHeartbeatUtc>DATEADD(second,-30,SYSUTCDATETIME()))";
        admission.Parameters.Add(new("@id",SqlDbType.UniqueIdentifier){Value=envelope.EntityId});
        if (state is "Queued" or "RetryWaiting" && (int)(await admission.ExecuteScalarAsync(ct))! == 0) state = "Held";
        return new DispatchState(state, attemptExists, intent);
    }, ct);

    private async ValueTask<T> RunAsync<T>(Func<SqlConnection, ValueTask<T>> action, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            await using var identity = connection.CreateCommand();
            identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
            return await action(connection);
        }
        catch (SqlException) { throw new WorkerDockException("ProjectionStoreUnavailable"); }
    }
}
