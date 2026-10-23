using Microsoft.Data.SqlClient;
using System.Data;
using WorkerDock.Infrastructure.Persistence;
namespace WorkerDock.IntegrationTests;
internal static class SqlCapacityTestSetup
{
    public static async Task ReadyAsync(TenantDatabaseOptions options,params string[] boots)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE [workerdock].TemplateCatalog SET ConcurrencyLimit=3 WHERE Enabled=1";
        await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();
        command.CommandText = "UPDATE [workerdock].RuntimeStatus SET BootId=@boot,Ready=1,HostCeiling=1,LastHeartbeatUtc=SYSUTCDATETIME() WHERE StateId=1";
        command.Parameters.Add(new SqlParameter("@boot",SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        await command.ExecuteNonQueryAsync();
    }
}
