using Microsoft.Data.SqlClient;
using System.Data;
using WorkerDock.Infrastructure.Migrations;

namespace WorkerDock.IntegrationTests;

public sealed class TenantSqlFixture : IAsyncDisposable
{
    public SqlDatabaseFixture Database { get; } = new();
    public MigrationRunner Runner => new(Database.ConnectionString, Database.InstanceId, Database.ScriptsRoot);
    private readonly SqlConnectionStringBuilder runtime;
    public string RuntimeConnectionString => runtime.ConnectionString;

    public TenantSqlFixture() => runtime = TenantRuntimeConnection.ForDatabase(
        Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_RUNTIME"), Database.ConnectionString);

    public async Task CreateAsync(bool snapshot)
    {
        await Database.CreateAsync(snapshot);
        await Runner.ApplyAsync("tenant", "0001_initial", default);
        await using var connection = new SqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @name sysname=@RuntimeLogin;
            IF DATABASE_PRINCIPAL_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max)=N'CREATE USER '+QUOTENAME(@name)+N' FOR LOGIN '+QUOTENAME(@name)+N';';
                EXEC sys.sp_executesql @sql;
            END;
            IF NOT EXISTS(SELECT 1 FROM sys.database_role_members WHERE role_principal_id=DATABASE_PRINCIPAL_ID(N'WorkerDockRuntime') AND member_principal_id=DATABASE_PRINCIPAL_ID(@name))
            BEGIN
                DECLARE @grant nvarchar(max)=N'ALTER ROLE [WorkerDockRuntime] ADD MEMBER '+QUOTENAME(@name)+N';';
                EXEC sys.sp_executesql @grant;
            END;
            """;
        command.Parameters.Add(new SqlParameter("@RuntimeLogin", SqlDbType.NVarChar, 128) { Value = runtime.UserID });
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await Database.DisposeAsync();
}