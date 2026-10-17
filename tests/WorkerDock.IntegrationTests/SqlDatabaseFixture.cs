using Microsoft.Data.SqlClient;
namespace WorkerDock.IntegrationTests;
public sealed class SqlDatabaseFixture : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string databaseName = "WorkerDock_Test_" + Guid.NewGuid().ToString("N");
    private bool created;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ConnectionString { get; }
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
    public async Task CreateAsync(bool snapshot)
    {
        await using var connection = new SqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync();
        created = true;
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = 160; ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT {(snapshot ? "ON" : "OFF")};";
        await command.ExecuteNonQueryAsync();
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
