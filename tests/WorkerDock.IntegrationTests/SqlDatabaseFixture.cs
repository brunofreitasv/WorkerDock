using Microsoft.Data.SqlClient;
using System.Text.Json;
using WorkerDock.Infrastructure.Migrations;
namespace WorkerDock.IntegrationTests;
public sealed class SqlDatabaseFixture : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string databaseName = "WorkerDock_Test_" + Guid.NewGuid().ToString("N");
    private bool created;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ConnectionString { get; }
    public string RuntimePrincipal { get; private set; } = "";
    public string ScriptsRoot => Path.Combine(AppContext.BaseDirectory, "database");
    public SqlDatabaseFixture()
    {
        var configured = Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.");
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master", ConnectTimeout = 5 };
        adminConnection = builder.ConnectionString;
        builder.InitialCatalog = databaseName;
        ConnectionString = builder.ConnectionString;
    }
    public async Task CreateAsync(bool snapshot, string? runtimeLoginName = null)
    {
        await using var connection = new SqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
        created = true;
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = 160; ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT {(snapshot ? "ON" : "OFF")};";
        await command.ExecuteNonQueryAsync();
        RuntimePrincipal = string.IsNullOrWhiteSpace(runtimeLoginName)
            ? "WorkerDockTestRuntime"
            : runtimeLoginName;
        await using var databaseConnection = new SqlConnection(ConnectionString);
        await databaseConnection.OpenAsync();
        await using var principal = databaseConnection.CreateCommand();
        principal.CommandText = """
            DECLARE @sql nvarchar(max)=N'CREATE USER '+QUOTENAME(@RuntimePrincipal)+
                CASE WHEN @RuntimeLoginName IS NULL THEN N' WITHOUT LOGIN' ELSE N' FOR LOGIN '+QUOTENAME(@RuntimeLoginName) END+N';';
            EXEC sys.sp_executesql @sql;
            """;
        principal.Parameters.Add(new SqlParameter("@RuntimePrincipal", System.Data.SqlDbType.NVarChar, 128) { Value = RuntimePrincipal });
        principal.Parameters.Add(new SqlParameter("@RuntimeLoginName", System.Data.SqlDbType.NVarChar, 128)
            { Value = runtimeLoginName is null ? DBNull.Value : runtimeLoginName });
        await principal.ExecuteNonQueryAsync();
    }
    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }
    public async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
    public async Task ApplyLatestSchemaAsync()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(ScriptsRoot, "tenant", "manifest.json")));
        var target = manifest.RootElement.EnumerateArray().Last().GetProperty("id").GetString()!;
        await new MigrationRunner(ConnectionString, InstanceId, RuntimePrincipal, ScriptsRoot).ApplyAsync("tenant", target, CancellationToken.None);
    }
    public async ValueTask DisposeAsync()
    {
        if (!created) return;
        if (!databaseName.StartsWith("WorkerDock_Test_", StringComparison.Ordinal) || databaseName.Length != 48)
            throw new InvalidOperationException("Invalid disposable database name.");
        await using var connection = new SqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
    }
}
