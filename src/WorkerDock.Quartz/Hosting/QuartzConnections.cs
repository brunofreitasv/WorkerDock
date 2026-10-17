using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;

namespace WorkerDock.Quartz.Hosting;

public sealed record QuartzConnections(string ConnectionString, Guid InstanceId)
{
    internal async ValueTask VerifyAsync(Guid instanceId, CancellationToken ct)
    {
        if (instanceId != InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
        try
        {
            var settings = new SqlConnectionStringBuilder(ConnectionString);
            settings.ConnectTimeout = Math.Clamp(settings.ConnectTimeout, 1, 10);
            await using var connection = new SqlConnection(settings.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            command.CommandText = "SELECT InstanceId FROM dbo.TenantSettings; SELECT COUNT(*) FROM dbo.SchemaHistory WHERE MigrationId = N'0002_quartz';";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetGuid(0) != InstanceId || await reader.ReadAsync(ct))
                throw new WorkerDockException("TenantInstanceMismatch");
            if (!await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct) || reader.GetInt32(0) != 1)
                throw new WorkerDockException("QuartzSchemaUnavailable");
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        catch (ArgumentException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
}
