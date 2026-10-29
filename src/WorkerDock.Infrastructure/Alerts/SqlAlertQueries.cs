using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class SqlAlertQueries(TenantDatabaseOptions options) : IAlertQueries
{
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(90);
    private static readonly string[] DeliveryStatuses = ["Pending", "Leased", "Delivered", "Failed", "Cancelled"];
    private static readonly string[] DeliveryKinds = ["Open", "Reminder", "Resolve"];

    public ValueTask<Page<AlertRuleOptionDto>> RuleOptionsAsync(CancellationToken ct) => RunAsync<Page<AlertRuleOptionDto>>(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, "SELECT RuleId,Name FROM [workerdock].AlertRules ORDER BY Name,RuleId;");
        var items = new List<AlertRuleOptionDto>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) items.Add(new(reader.GetGuid(0), reader.GetString(1)));
        return new Page<AlertRuleOptionDto>(items, null);
    }, ct);

    public ValueTask<Page<AlertIncidentDto>> IncidentsAsync(IncidentFilter filter, PageRequest page, CancellationToken ct)
    {
        Validate(filter, page);
        var filterKey = IncidentFilterKey(filter);
        var cursor = HistoryCursorCodec.Decode(page.Cursor, options.InstanceId, "alert-incidents", filterKey);
        return RunAsync<Page<AlertIncidentDto>>(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                WITH source AS (
                    SELECT i.IncidentId,i.RuleId,r.Name AS RuleName,i.Condition,i.SubjectKey,i.State,i.OpenedAtUtc,i.LastObservedAtUtc,i.ResolvedAtUtc,
                           latest.SafeCode,
                           CASE WHEN i.State=N'Resolved' AND i.ResolvedAtUtc>i.LastObservedAtUtc THEN i.ResolvedAtUtc ELSE i.LastObservedAtUtc END AS ActivityAtUtc,
                           CASE i.State WHEN N'Open' THEN 0 WHEN N'Unknown' THEN 1 ELSE 2 END AS StateRank
                    FROM [workerdock].AlertIncidents i
                    JOIN [workerdock].AlertRules r ON r.RuleId=i.RuleId
                    OUTER APPLY(SELECT TOP(1) d.SafeCode FROM [workerdock].AlertDeliveries d
                        WHERE d.IncidentId=i.IncidentId AND d.SafeCode IS NOT NULL ORDER BY d.CreatedAtUtc DESC,d.DeliveryId DESC) latest
                    WHERE ((@state=N'Active' AND i.State IN(N'Open',N'Unknown')) OR (@state=N'Resolved' AND i.State=N'Resolved') OR @state=N'All')
                      AND (@ruleId IS NULL OR i.RuleId=@ruleId)
                ), filtered AS (
                    SELECT * FROM source
                    WHERE (@from IS NULL OR ActivityAtUtc>=@from)
                      AND (@to IS NULL OR ActivityAtUtc<@to)
                ), selected AS (
                    SELECT TOP(@take) * FROM filtered
                    WHERE (@cursorRank IS NULL OR StateRank>@cursorRank OR (StateRank=@cursorRank AND
                        (ActivityAtUtc<@cursorTime OR (ActivityAtUtc=@cursorTime AND IncidentId<@cursorId))))
                    ORDER BY StateRank,ActivityAtUtc DESC,IncidentId DESC
                )
                SELECT IncidentId,RuleId,RuleName,Condition,SubjectKey,State,OpenedAtUtc,LastObservedAtUtc,ResolvedAtUtc,SafeCode,ActivityAtUtc,StateRank
                FROM selected ORDER BY StateRank,ActivityAtUtc DESC,IncidentId DESC;
                """);
            Add(command, "@take", SqlDbType.Int, page.Limit + 1);
            Add(command, "@state", SqlDbType.NVarChar, filter.State, 12);
            Add(command, "@ruleId", SqlDbType.UniqueIdentifier, (object?)filter.RuleId ?? DBNull.Value);
            Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc is null ? DBNull.Value : filter.FromUtc.Value.UtcDateTime);
            Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc is null ? DBNull.Value : filter.ToUtc.Value.UtcDateTime);
            Add(command, "@cursorRank", SqlDbType.Int, cursor is null ? DBNull.Value : cursor.Rank);
            Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Time.UtcDateTime);
            Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor is null ? DBNull.Value : cursor.Id);
            var rows = new List<IncidentRow>();
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) rows.Add(new(
                    new AlertIncidentDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                        Utc(reader.GetDateTime(6)), Utc(reader.GetDateTime(7)), reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : reader.GetString(9)),
                    Utc(reader.GetDateTime(10)), reader.GetInt32(11)));
            var hasMore = rows.Count > page.Limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var next = hasMore ? HistoryCursorCodec.Encode(options.InstanceId, "alert-incidents", filterKey, rows[^1].ActivityAtUtc, rows[^1].Item.IncidentId, rows[^1].StateRank) : null;
            return new Page<AlertIncidentDto>(rows.Select(x => x.Item).ToArray(), next);
        }, ct);
    }

    public ValueTask<Page<AlertDeliveryDto>> DeliveriesAsync(DeliveryFilter filter, PageRequest page, CancellationToken ct)
    {
        Validate(filter, page);
        var filterKey = DeliveryFilterKey(filter);
        var cursor = HistoryCursorCodec.Decode(page.Cursor, options.InstanceId, "alert-deliveries", filterKey);
        return RunAsync<Page<AlertDeliveryDto>>(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                SELECT TOP(@take) d.DeliveryId,d.IncidentId,r.Name,i.SubjectKey,d.Kind,d.Status,d.AttemptCount,d.NextAvailableAtUtc,d.CompletedAtUtc,d.SafeCode,d.CreatedAtUtc
                FROM [workerdock].AlertDeliveries d
                JOIN [workerdock].AlertIncidents i ON i.IncidentId=d.IncidentId
                JOIN [workerdock].AlertRules r ON r.RuleId=i.RuleId
                WHERE d.CreatedAtUtc>=@from AND d.CreatedAtUtc<@to
                  AND (@status IS NULL OR d.Status=@status)
                  AND (@kind IS NULL OR d.Kind=@kind)
                  AND (@cursorTime IS NULL OR d.CreatedAtUtc<@cursorTime OR (d.CreatedAtUtc=@cursorTime AND d.DeliveryId<@cursorId))
                ORDER BY d.CreatedAtUtc DESC,d.DeliveryId DESC;
                """);
            Add(command, "@take", SqlDbType.Int, page.Limit + 1);
            Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc.UtcDateTime);
            Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc.UtcDateTime);
            Add(command, "@status", SqlDbType.NVarChar, (object?)filter.Status ?? DBNull.Value, 16);
            Add(command, "@kind", SqlDbType.NVarChar, (object?)filter.Kind ?? DBNull.Value, 16);
            Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Time.UtcDateTime);
            Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor is null ? DBNull.Value : cursor.Id);
            var rows = new List<DeliveryRow>();
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) rows.Add(new(
                    new AlertDeliveryDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                        Utc(reader.GetDateTime(7)), reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : reader.GetString(9)),
                    Utc(reader.GetDateTime(10))));
            var hasMore = rows.Count > page.Limit;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var next = hasMore ? HistoryCursorCodec.Encode(options.InstanceId, "alert-deliveries", filterKey, rows[^1].CreatedAtUtc, rows[^1].Item.DeliveryId) : null;
            return new Page<AlertDeliveryDto>(rows.Select(x => x.Item).ToArray(), next);
        }, ct);
    }

    private async ValueTask<T> RunAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await using var identity = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                return await action(connection, transaction, token);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static void Validate(IncidentFilter filter, PageRequest page)
    {
        if (page.Limit is < 1 or > 100 || filter.State is not ("Active" or "All" or "Resolved") ||
            (filter.FromUtc is null) != (filter.ToUtc is null) || filter.FromUtc is { } from && filter.ToUtc is { } to &&
            (to <= from || to - from > MaxRange) || filter.RuleId == Guid.Empty)
            throw new WorkerDockException("ValidationFailed");
    }

    private static void Validate(DeliveryFilter filter, PageRequest page)
    {
        if (page.Limit is < 1 or > 100 || filter.ToUtc <= filter.FromUtc || filter.ToUtc - filter.FromUtc > MaxRange ||
            filter.Status is not null && !DeliveryStatuses.Contains(filter.Status, StringComparer.Ordinal) ||
            filter.Kind is not null && !DeliveryKinds.Contains(filter.Kind, StringComparer.Ordinal))
            throw new WorkerDockException("ValidationFailed");
    }

    private static string IncidentFilterKey(IncidentFilter filter) => JsonSerializer.Serialize(new
    {
        fromUtc = filter.FromUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        toUtc = filter.ToUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        state = filter.State,
        ruleId = filter.RuleId
    });

    private static string DeliveryFilterKey(DeliveryFilter filter) => JsonSerializer.Serialize(new
    {
        fromUtc = filter.FromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        toUtc = filter.ToUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        status = filter.Status,
        kind = filter.Kind
    });

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Add(DbCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type switch
        { SqlDbType.Int => DbType.Int32, SqlDbType.UniqueIdentifier => DbType.Guid, SqlDbType.DateTime2 => DbType.DateTime2, SqlDbType.NVarChar => DbType.String, _ => DbType.Object };
        if (size > 0) parameter.Size = size;
        parameter.Value = value; command.Parameters.Add(parameter);
    }

    private sealed record IncidentRow(AlertIncidentDto Item, DateTimeOffset ActivityAtUtc, int StateRank);
    private sealed record DeliveryRow(AlertDeliveryDto Item, DateTimeOffset CreatedAtUtc);
}
