using System.Data;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Runtime;

public sealed record RuntimeHeartbeat(Guid BootId, bool Ready, DateTimeOffset LastHeartbeatUtc, int HostCeiling);

public sealed class SqlRuntimeStatus(TenantDatabaseOptions options, int hostCeiling)
{
    public async ValueTask HeartbeatAsync(Guid bootId, bool ready, CancellationToken ct)
    {
        if (bootId == Guid.Empty || hostCeiling is < 1 or > 1000) throw new WorkerDockException("WorkerConfigurationUnavailable");
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [workerdock].RuntimeStatus SET BootId=@boot,Ready=@ready,HostCeiling=@ceiling,LastHeartbeatUtc=SYSUTCDATETIME() WHERE StateId=1;";
            command.Parameters.Add(new SqlParameter("@boot", SqlDbType.UniqueIdentifier) { Value = bootId });
            command.Parameters.Add(new SqlParameter("@ready", SqlDbType.Bit) { Value = ready });
            command.Parameters.Add(new SqlParameter("@ceiling", SqlDbType.Int) { Value = hostCeiling });
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new WorkerDockException("RuntimeStatusUnavailable");
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<RuntimeHeartbeat?> ReadAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT BootId,Ready,LastHeartbeatUtc,HostCeiling FROM [workerdock].RuntimeStatus WHERE StateId=1";
            await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.IsDBNull(0) || row.IsDBNull(2)) return null;
            return new(row.GetGuid(0), row.GetBoolean(1), new DateTimeOffset(DateTime.SpecifyKind(row.GetDateTime(2), DateTimeKind.Utc)), row.GetInt32(3));
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var identity = connection.CreateCommand();
            identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}
