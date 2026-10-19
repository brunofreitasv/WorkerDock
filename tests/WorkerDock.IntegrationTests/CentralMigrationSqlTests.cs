using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;

namespace WorkerDock.IntegrationTests;

public sealed class CentralMigrationSqlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Central_registry_migration_binds_identity_and_supports_empty_up_down_up(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);

        await runner.ApplyAsync("central", "0001_registry", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory WHERE MigrationId=N'0001_registry';"));
        Assert.Equal(database.InstanceId.ToString(), await ScalarTextAsync(database, "SELECT CONVERT(nvarchar(36),CentralInstanceId) FROM [workerdock].CentralSettings WHERE Singleton=1;"));
        Assert.Equal(5, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock');"));

        var wrong = new MigrationRunner(database.ConnectionString, Guid.NewGuid(), database.ScriptsRoot);
        var mismatch = await Assert.ThrowsAsync<WorkerDockException>(async () => await wrong.ApplyAsync("central", "0001_registry", default));
        Assert.Equal("CentralInstanceMismatch", mismatch.Code);

        await runner.RollbackAsync("central", "0001_registry", default);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.schemas WHERE name=N'workerdock';"));
        await runner.ApplyAsync("central", "0001_registry", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory WHERE MigrationId=N'0001_registry';"));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Central_rollback_refuses_registered_links_and_append_only_audit()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot: false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("central", "0001_registry", default);
        await database.ExecuteAsync("INSERT [workerdock].TenantRegistrations(Id,DisplayName,ExpectedInstanceId,DashboardUrl,HealthCredentialReference) VALUES(NEWID(),N'Finance',NEWID(),N'https://finance.example.test/',N'FINANCE_HEALTH');");

        var rollback = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.RollbackAsync("central", "0001_registry", default));
        Assert.Equal("UnsafeRollback", rollback.Code);
        await database.ExecuteAsync("INSERT [workerdock].AuditEvents(Id,ActorId,AuthenticationMode,Action,CorrelationId) VALUES(NEWID(),N'test',N'Anonymous',N'TenantRegistered',NEWID());");
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync("UPDATE [workerdock].AuditEvents SET Action='Tampered';"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TenantRegistrations;"));
    }

    private static async Task<string> ScalarTextAsync(SqlDatabaseFixture database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
