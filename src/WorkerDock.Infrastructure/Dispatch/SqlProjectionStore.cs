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

    private ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadAsync(int limit, bool published, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return RunAsync<IReadOnlyList<ProjectionEnvelope>>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TOP (@limit) EventId,EventType,SchemaVersion,EntityId,EntityRevision,PayloadDigest,AvailableAtUtc,PayloadJson FROM dbo.Outbox WHERE " +
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
            command.CommandText = "UPDATE dbo.Outbox SET DispatchedAtUtc=COALESCE(DispatchedAtUtc,SYSUTCDATETIME()),ProjectionState=CASE WHEN ProjectionState=N'Retired' THEN N'Retired' ELSE N'Published' END,LastErrorCode=NULL,RetryAtUtc=NULL WHERE EventId=@id AND PayloadDigest=@digest";
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
            command.CommandText = "UPDATE dbo.Outbox SET ProjectionState=CASE WHEN DispatchedAtUtc IS NULL THEN N'Failed' ELSE N'Published' END,LastErrorCode=@code,RetryAtUtc=@retry,ReviewAtUtc=@retry WHERE EventId=@id AND ProjectionState<>N'Retired'";
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
            command.CommandText = "UPDATE dbo.Outbox SET ProjectionState=CASE WHEN @retired=1 THEN N'Retired' ELSE ProjectionState END,ReviewAtUtc=@review WHERE EventId=@id AND DispatchedAtUtc IS NOT NULL";
            command.Parameters.Add(new("@id", SqlDbType.UniqueIdentifier) { Value = intentId });
            command.Parameters.Add(new("@retired", SqlDbType.Bit) { Value = retired });
            command.Parameters.Add(new("@review", SqlDbType.DateTime2) { Value = reviewAtUtc.UtcDateTime });
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public ValueTask<DispatchState> ReadAsync(ProjectionEnvelope envelope, int attemptOrdinal, CancellationToken ct) => RunAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.State,e.JobRevision,r.TemplateId,e.AvailableAtUtc,COALESCE((SELECT MAX(a.AttemptNumber) FROM dbo.Attempts a WHERE a.ExecutionId=e.ExecutionId),0),COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.Priority')),5) FROM dbo.Executions e JOIN dbo.JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision WHERE e.ExecutionId=@execution";
        command.Parameters.Add(new("@execution", SqlDbType.UniqueIdentifier) { Value = envelope.EntityId });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt64(1) != envelope.EntityRevision || attemptOrdinal < 1 || attemptOrdinal > reader.GetInt32(4) + 1)
            throw new WorkerDockException("ProjectionIntegrityError");
        var intent = new DispatchIntent(envelope.IntentId, envelope.EntityId, attemptOrdinal, reader.GetString(2),
            new(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)), reader.GetInt32(5), reader.GetInt64(1), "");
        intent = intent with { PayloadDigest = intent.ComputeDigest() };
        return new DispatchState(reader.GetString(0), reader.GetInt32(4) >= attemptOrdinal, intent);
    }, ct);

    private async ValueTask<T> RunAsync<T>(Func<SqlConnection, ValueTask<T>> action, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            await using var identity = connection.CreateCommand();
            identity.CommandText = "SELECT InstanceId FROM dbo.TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
            return await action(connection);
        }
        catch (SqlException) { throw new WorkerDockException("ProjectionStoreUnavailable"); }
    }
}
