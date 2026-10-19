using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Observability;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Observability;

public sealed class SqlExecutionEventQueries(TenantDatabaseOptions options) : IExecutionEventQueries
{
    public async ValueTask<ExecutionEventPage> ReadAsync(Guid executionId, string? cursor, int limit, CancellationToken ct)
    {
        if (executionId == Guid.Empty || limit is < 1 or > 100 || cursor is { Length: > 128 }) throw new WorkerDockException("ValidationFailed");
        var after = DecodeCursor(cursor);
        await using var connection = new SqlConnection(options.ConnectionString);
        try { await connection.OpenAsync(ct); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
        }
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Executions WHERE ExecutionId=@id) THEN 1 ELSE 0 END";
            exists.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = executionId;
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0) throw new WorkerDockException("ExecutionNotFound");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH Events AS
            (
                SELECT EventId,ExecutionId,AttemptId,CONVERT(nvarchar(20),N'Lifecycle') Source,CONVERT(bigint,0) Sequence,OccurredAtUtc AtUtc,State Code,CONVERT(nvarchar(2048),N'{}') SafeFieldsJson
                  FROM [workerdock].AttemptStatusEvents WHERE ExecutionId=@id
                UNION ALL
                SELECT EventId,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson
                  FROM [workerdock].ExecutionEvents WHERE ExecutionId=@id
            )
            SELECT TOP(@take) EventId,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson
              FROM Events
             WHERE (@cursorAt IS NULL OR AtUtc>@cursorAt OR (AtUtc=@cursorAt AND EventId>@cursorId))
             ORDER BY AtUtc,EventId;
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = executionId;
        command.Parameters.Add("@take", SqlDbType.Int).Value = limit + 1;
        command.Parameters.Add("@cursorAt", SqlDbType.DateTime2).Value = (object?)after?.AtUtc.UtcDateTime ?? DBNull.Value;
        command.Parameters.Add("@cursorId", SqlDbType.UniqueIdentifier).Value = (object?)after?.EventId ?? DBNull.Value;
        var items = new List<ExecutionEventDto>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                IReadOnlyDictionary<string, string> fields;
                try { fields = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7)) ?? new Dictionary<string, string>(); }
                catch { fields = new Dictionary<string, string>(); }
                items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4), Utc(reader.GetDateTime(5)), reader.GetString(6), fields));
            }
        }
        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var next = hasMore && items.Count > 0 ? EncodeCursor(items[^1].AtUtc, items[^1].EventId) : null;
        return new(items, next);
    }

    private static (DateTimeOffset AtUtc, Guid EventId)? DecodeCursor(string? cursor)
    {
        if (cursor is null) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out var id)) throw new FormatException();
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch { throw new WorkerDockException("ValidationFailed"); }
    }

    private static string EncodeCursor(DateTimeOffset atUtc, Guid eventId) => Convert.ToBase64String(Encoding.UTF8.GetBytes(atUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + eventId.ToString("N")));
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
