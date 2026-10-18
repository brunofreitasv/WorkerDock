using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Dispatch;

public sealed class SqlProjectionStore(TenantDatabaseOptions options) : IProjectionStore, IDispatchStateStore, IProjectionReviewStore
{
    public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPendingAsync(int limit, CancellationToken ct) => ReadAsync(limit, false, ct);
    public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPublishedAsync(int limit, CancellationToken ct) => ReadAsync(limit, true, ct);

    private async ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadAsync(int limit, bool published, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await RunAsync(async connection =>
        {
            await using var jobs = connection.CreateCommand();
            jobs.CommandText = "SELECT JobId FROM [workerdock].Jobs WHERE Archived=0";
            var ids = new List<Guid>();
            await using (var reader = await jobs.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
            foreach (var id in ids) await new JobAdmission(options).AdmitAsync(id, ct);
            return true;
        }, ct);
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
        await RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [workerdock].Outbox SET DispatchedAtUtc=COALESCE(DispatchedAtUtc,SYSUTCDATETIME()),ProjectionState=CASE WHEN ProjectionState=N'Retired' THEN N'Retired' ELSE N'Published' END,LastErrorCode=NULL,RetryAtUtc=NULL WHERE EventId=@id AND PayloadDigest=@digest";
            command.Parameters.Add(new("@id", SqlDbType.UniqueIdentifier) { Value = intentId });
            command.Parameters.Add(new("@digest", SqlDbType.Char, 64) { Value = payloadDigest });
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new WorkerDockException("ProjectionIntegrityError");
            return true;
        }, ct);
    }

    public async ValueTask RecordFailureAsync(Guid intentId, string safeCode, DateTimeOffset retryAtUtc, CancellationToken ct)
    {
        if (safeCode is not ("UnsupportedProjection" or "ProjectionIntegrityError" or "ProjectionTriggerError" or
            "ProjectionPublicationRace" or "ProjectionHeld" or "QuartzStoreUnavailable" or "ProjectionStoreUnavailable"))
            throw new ArgumentException("Unknown projection failure code.", nameof(safeCode));
        await RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [workerdock].Outbox SET ProjectionState=CASE WHEN @code=N'ProjectionHeld' THEN N'Held' WHEN DispatchedAtUtc IS NULL THEN N'Failed' ELSE N'Published' END,LastErrorCode=@code,RetryAtUtc=@retry,ReviewAtUtc=@retry WHERE EventId=@id AND ProjectionState<>N'Retired'";
            command.Parameters.Add(new("@id", SqlDbType.UniqueIdentifier) { Value = intentId });
            command.Parameters.Add(new("@code", SqlDbType.NVarChar, 40) { Value = safeCode });
            command.Parameters.Add(new("@retry", SqlDbType.DateTime2) { Value = retryAtUtc.UtcDateTime });
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);
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
        admission.CommandText = "SELECT COUNT(*) FROM [workerdock].Executions e JOIN [workerdock].Jobs j ON j.JobId=e.JobId JOIN [workerdock].JobPermits permit ON permit.ExecutionId=e.ExecutionId JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision JOIN [workerdock].TemplatePolicies p ON p.TemplateId=r.TemplateId WHERE e.ExecutionId=@id AND j.Enabled=1 AND j.Archived=0 AND p.StartGateClosed=0 AND p.EffectiveLimit>0 AND p.EffectiveRevision=@epoch AND EXISTS(SELECT 1 FROM [workerdock].Workers w WHERE w.State=N'Ready' AND w.AcknowledgedRevision=p.EffectiveRevision             AND w.CatalogDigest=(SELECT CatalogDigest FROM [workerdock].PolicyRevisions WHERE PolicyKind=N'Capacity')             AND w.LastHeartbeatUtc>DATEADD(second,-60,SYSUTCDATETIME())             AND w.LiveLimit>(SELECT COUNT(*) FROM [workerdock].Attempts a WHERE a.WorkerBootId=w.BootId AND a.State=N'Running'))";
        admission.Parameters.Add(new("@id",SqlDbType.UniqueIdentifier){Value=envelope.EntityId}); admission.Parameters.Add(new("@epoch",SqlDbType.BigInt){Value=capacityRevision});
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
