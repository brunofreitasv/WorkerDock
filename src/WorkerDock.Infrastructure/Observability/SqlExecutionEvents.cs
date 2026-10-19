using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Observability;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Observability;

public sealed class SqlExecutionEvents(TenantDatabaseOptions options) : IExecutionEvents
{
    private static readonly HashSet<string> Sources = new(StringComparer.Ordinal) { "Lifecycle", "Supervisor", "Handler" };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "TemplateStarted", "TemplateCompleted", "TemplateFailed", "SecretUnavailable", "ExecutorDisconnected", "ExecutorStarted"
    };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "executionId", "attemptId", "correlationId", "durationMs", "affectedRows", "outcome", "safeCode"
    };

    public async ValueTask AppendAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
    {
        if (events.Count > 100) throw new WorkerDockException("ValidationFailed");
        if (events.Count == 0) return;
        var payloads = events.Select(ValidateAndSerialize).ToArray();
        await using var connection = new SqlConnection(options.ConnectionString);
        try { await connection.OpenAsync(ct); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            for (var i = 0; i < events.Count; i++)
            {
                var item = events[i]; var command = connection.CreateCommand(); command.Transaction = transaction;
                await using (command)
                {
                    command.CommandText = """
                        IF NOT EXISTS(SELECT 1 FROM [workerdock].ExecutionEvents WITH(UPDLOCK,HOLDLOCK)
                            WHERE ExecutionId=@execution AND AttemptScopeId=ISNULL(@attempt,CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000')) AND Source=@source AND Sequence=@sequence)
                        INSERT [workerdock].ExecutionEvents(EventId,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson)
                        VALUES(@event,@execution,@attempt,@source,@sequence,@at,@code,@fields);
                        """;
                    Add(command, "@event", SqlDbType.UniqueIdentifier, Guid.NewGuid());
                    Add(command, "@execution", SqlDbType.UniqueIdentifier, item.ExecutionId);
                    Add(command, "@attempt", SqlDbType.UniqueIdentifier, (object?)item.AttemptId ?? DBNull.Value);
                    Add(command, "@source", SqlDbType.NVarChar, item.Source, 20);
                    Add(command, "@sequence", SqlDbType.BigInt, item.Sequence);
                    Add(command, "@at", SqlDbType.DateTime2, item.AtUtc.UtcDateTime);
                    Add(command, "@code", SqlDbType.NVarChar, item.Code, 80);
                    Add(command, "@fields", SqlDbType.NVarChar, payloads[i], 2048);
                    await command.ExecuteNonQueryAsync(ct);
                }
            }
            await transaction.CommitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SqlException) { await transaction.RollbackAsync(ct); throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static string ValidateAndSerialize(ExecutionEvent item)
    {
        if (item.ExecutionId == Guid.Empty || item.AttemptId == Guid.Empty || !Sources.Contains(item.Source) || item.Sequence <= 0 ||
            item.AtUtc < DateTimeOffset.UnixEpoch || item.AtUtc > DateTimeOffset.UtcNow.AddMinutes(5) || !Codes.Contains(item.Code) || item.SafeFields.Count > 8)
            throw new WorkerDockException("ValidationFailed");
        foreach (var (key, value) in item.SafeFields)
        {
            if (!Fields.Contains(key) || value is not { Length: > 0 and <= 80 }) throw new WorkerDockException("ValidationFailed");
            var valid = key switch
            {
                "executionId" or "attemptId" or "correlationId" => Guid.TryParseExact(value, "D", out _),
                "durationMs" or "affectedRows" => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0,
                "outcome" => value is "Success" or "TransientFailure" or "PermanentFailure",
                "safeCode" => value.All(char.IsAsciiLetterOrDigit) && Codes.Contains(value),
                _ => false
            };
            if (!valid) throw new WorkerDockException("ValidationFailed");
        }
        var json = JsonSerializer.Serialize(item.SafeFields);
        if (json.Length > 2048) throw new WorkerDockException("ValidationFailed");
        return json;
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = command.Parameters.Add(name, type); if (size > 0) parameter.Size = size; parameter.Value = value;
    }
}
