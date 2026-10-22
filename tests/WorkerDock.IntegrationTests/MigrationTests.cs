using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Infrastructure.Persistence;
namespace WorkerDock.IntegrationTests;
public sealed class MigrationTests
{
    [Theory]
    [InlineData("DELETE FROM [workerdock].SchemaHistory WHERE MigrationId='0001_bootstrap'", "InvalidMigrationHistory")]
    [InlineData("UPDATE [workerdock].SchemaHistory SET MigrationId='9999_unknown' WHERE MigrationId='0002_quartz'", "InvalidMigrationHistory")]
    [InlineData("DELETE FROM [workerdock].SchemaHistory", "InvalidMigrationHistory")]
    [InlineData("UPDATE [workerdock].SchemaHistory SET UpChecksum=REPLICATE('0',64) WHERE MigrationId='0002_quartz'", "MigrationChecksumMismatch")]
    [InlineData("UPDATE [workerdock].SchemaHistory SET DownChecksum=REPLICATE('0',64) WHERE MigrationId='0002_quartz'", "MigrationChecksumMismatch")]
    [Trait("Store", "Sql")]
    public async Task History_hole_or_unknown_row_blocks_next_migration_before_write(string sql, string expectedCode)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0002_quartz", default);
        await database.ExecuteAsync(sql);
        var before = await ReadHistoryAsync(database);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0003_execution", default));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(before, await ReadHistoryAsync(database));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'[workerdock].Executions')"));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Wrong_instance_blocks_next_migration_before_write()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_bootstrap", default);
        var before = await ReadHistoryAsync(database);
        var wrong = new MigrationRunner(database.ConnectionString, Guid.NewGuid(), database.ScriptsRoot);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await wrong.ApplyAsync("tenant", "0002_quartz", default));

        Assert.Equal("TenantInstanceMismatch", error.Code);
        Assert.Equal(before, await ReadHistoryAsync(database));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Target_behind_installed_history_is_rejected()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0002_quartz", default);
        var before = await ReadHistoryAsync(database);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_bootstrap", default));

        Assert.Equal("MigrationTargetBehindCurrent", error.Code);
        Assert.Equal(before, await ReadHistoryAsync(database));
    }

    [Theory]
    [InlineData("CREATE SCHEMA [workerdock]")]
    [InlineData("EXEC(N'CREATE SCHEMA [workerdock]'); EXEC(N'CREATE TABLE [workerdock].SchemaHistory(MigrationId nvarchar(100))')")]
    [InlineData("EXEC(N'CREATE SCHEMA [workerdock]'); EXEC(N'CREATE TABLE [workerdock].TenantSettings(InstanceId uniqueidentifier)')")]
    [Trait("Store", "Sql")]
    public async Task Partial_schema_is_rejected_before_bootstrap_writes(string sql)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ExecuteAsync(sql);
        var before = await database.ScalarAsync("SELECT COUNT(*) FROM sys.objects WHERE schema_id=SCHEMA_ID(N'workerdock')");
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_bootstrap", default));

        Assert.Equal("InvalidMigrationHistory", error.Code);
        Assert.Equal(before, await database.ScalarAsync("SELECT COUNT(*) FROM sys.objects WHERE schema_id=SCHEMA_ID(N'workerdock')"));
    }

    private static async Task<string> ReadHistoryAsync(SqlDatabaseFixture database)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId, UpChecksum, DownChecksum, AppliedAtUtc FROM [workerdock].SchemaHistory ORDER BY MigrationId FOR JSON PATH";
        return (string)(await command.ExecuteScalarAsync())!;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Fresh_custom_schema_installs_and_rolls_back_all_pairs(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0004_templates", default);
        Assert.Equal(8, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
        Assert.Equal(30, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock') AND name LIKE 'WD[SE][_]QRTZ[_]%'"));
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped=0 AND schema_id<>SCHEMA_ID(N'workerdock')"));
        var refused = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0001_bootstrap", default));
        Assert.Equal("UnsafeRollback", refused.Code);
        Assert.Equal(8, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
        foreach (var id in new[] { "0004_templates", "0003d_dispatch", "0003c_capacity", "0003b_attempts", "0003a_projection", "0003_execution", "0002_quartz", "0001_bootstrap" })
            await runner.RollbackAsync("tenant", id, default);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.schemas WHERE name=N'workerdock'"));
        await runner.ApplyAsync("tenant", "0004_templates", default);
        Assert.Equal(8, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Checksum_tamper_and_wrong_instance_fail_closed(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_bootstrap", default);
        var wrong = new MigrationRunner(database.ConnectionString, Guid.NewGuid(), database.ScriptsRoot);
        var wrongInstance = await Assert.ThrowsAsync<WorkerDockException>(async () => await wrong.ApplyAsync("tenant", "0001_bootstrap", default));
        Assert.Equal("TenantInstanceMismatch", wrongInstance.Code);
        var service = new TenantSettingsService(new(database.ConnectionString, Guid.NewGuid()));
        var wrongRead = await Assert.ThrowsAsync<WorkerDockException>(async () => await service.GetAsync(default));
        Assert.Equal("TenantInstanceMismatch", wrongRead.Code);
        await database.ExecuteAsync("UPDATE [workerdock].SchemaHistory SET UpChecksum = REPLICATE('0',64);");
        var tampered = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_bootstrap", default));
        Assert.Equal("MigrationChecksumMismatch", tampered.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Up_down_up_preserves_schema_contract(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_bootstrap", default);
        Assert.Equal(4, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock') AND name IN ('TenantSettings','AuditEvents','SchemaHistory','Outbox');"));
        await runner.RollbackAsync("tenant", "0001_bootstrap", default);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock') AND name IN ('TenantSettings','AuditEvents','SchemaHistory','Outbox');"));
        await runner.ApplyAsync("tenant", "0001_bootstrap", default);
        var service = new TenantSettingsService(new(database.ConnectionString, database.InstanceId));
        var settings = await service.GetAsync(default);
        await service.UpdateAsync(settings with { DisplayName = "Populated" }, settings.Version, new("issuer|subject", "Entra", Guid.NewGuid()), default);
        var rollback = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0001_bootstrap", default));
        Assert.Equal("UnsafeRollback", rollback.Code);
        Assert.Equal("Populated", (await service.GetAsync(default)).DisplayName);
    }

    [Theory]
    [InlineData("../tenant")]
    [InlineData("TENANT")]
    [InlineData("other")]
    public async Task Invalid_database_kind_fails_before_connecting(string kind)
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.GetTempPath());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync(kind, "0001_bootstrap", default));
        Assert.Equal("InvalidDatabaseKind", error.Code);
    }

}
