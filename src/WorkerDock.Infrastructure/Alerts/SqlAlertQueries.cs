using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class SqlAlertQueries(TenantDatabaseOptions options) : IAlertQueries
{
    public ValueTask<IReadOnlyList<AlertIncidentDto>> ListIncidentsAsync(int limit, CancellationToken ct)
    {
        ValidateLimit(limit);
        return RunAsync<IReadOnlyList<AlertIncidentDto>>(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                SELECT TOP(@limit) i.IncidentId,i.RuleId,r.Name,i.Condition,i.SubjectKey,i.State,i.OpenedAtUtc,
                    i.LastObservedAtUtc,i.ResolvedAtUtc,latest.SafeCode
                FROM [workerdock].AlertIncidents i
                JOIN [workerdock].AlertRules r ON r.RuleId=i.RuleId
                OUTER APPLY(SELECT TOP(1) d.SafeCode FROM [workerdock].AlertDeliveries d
                    WHERE d.IncidentId=i.IncidentId AND d.SafeCode IS NOT NULL ORDER BY d.CreatedAtUtc DESC,d.DeliveryId DESC) latest
                ORDER BY CASE i.State WHEN N'Open' THEN 0 WHEN N'Unknown' THEN 1 ELSE 2 END,
                    i.LastObservedAtUtc DESC,i.IncidentId DESC;
                """);
            Add(command, "@limit", SqlDbType.Int, limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            var result = new List<AlertIncidentDto>();
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    Utc(reader.GetDateTime(6)), Utc(reader.GetDateTime(7)), reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : reader.GetString(9)));
            return result;
        }, ct);
    }

    public ValueTask<IReadOnlyList<AlertDeliveryDto>> ListDeliveriesAsync(int limit, CancellationToken ct)
    {
        ValidateLimit(limit);
        return RunAsync<IReadOnlyList<AlertDeliveryDto>>(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                SELECT TOP(@limit) DeliveryId,IncidentId,Kind,Status,AttemptCount,NextAvailableAtUtc,CompletedAtUtc,SafeCode
                FROM [workerdock].AlertDeliveries ORDER BY CreatedAtUtc DESC,DeliveryId DESC;
                """);
            Add(command, "@limit", SqlDbType.Int, limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            var result = new List<AlertDeliveryDto>();
            while (await reader.ReadAsync(token))
                result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                    Utc(reader.GetDateTime(5)), reader.IsDBNull(6) ? null : Utc(reader.GetDateTime(6)), reader.IsDBNull(7) ? null : reader.GetString(7)));
            return result;
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

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 200) throw new WorkerDockException("ValidationFailed", "limit");
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Add(DbCommand command, string name, SqlDbType type, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type == SqlDbType.Int ? DbType.Int32 : DbType.Object; parameter.Value = value; command.Parameters.Add(parameter); }
}
