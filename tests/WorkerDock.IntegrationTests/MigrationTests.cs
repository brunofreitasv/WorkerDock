using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Infrastructure.Persistence;
namespace WorkerDock.IntegrationTests;

public sealed class MigrationTests
{
    [Fact]
    [Trait("Store", "Sql")]
    public async Task Fresh_install_creates_full_schema_and_guarded_rollback_succeeds_when_empty()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory WHERE MigrationId=N'0001_initial'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].TenantSettings WHERE InstanceId='" + database.InstanceId + "'"));
        Assert.True(await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'workerdock')") >= 30);
        await runner.RollbackAsync("tenant", "0001_initial", default);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.schemas WHERE name=N'workerdock'"));
        await runner.ApplyAsync("tenant", "0001_initial", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Wrong_instance_and_checksum_tampering_fail_closed()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        var wrong = new MigrationRunner(database.ConnectionString, Guid.NewGuid(), database.ScriptsRoot);
        var wrongInstance = await Assert.ThrowsAsync<WorkerDockException>(async () => await wrong.ApplyAsync("tenant", "0001_initial", default));
        Assert.Equal("TenantInstanceMismatch", wrongInstance.Code);
        await database.ExecuteAsync("UPDATE [workerdock].SchemaHistory SET UpChecksum=REPLICATE('0',64)");
        var tampered = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_initial", default));
        Assert.Equal("MigrationChecksumMismatch", tampered.Code);
    }

    [Fact]
    public async Task Unknown_target_is_rejected_before_connecting()
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.GetTempPath());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "missing", default));
        Assert.Equal("UnknownMigration", error.Code);
    }

    [Fact]
    public async Task Invalid_database_kind_fails_before_connecting()
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.GetTempPath());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("other", "0001_initial", default));
        Assert.Equal("InvalidDatabaseKind", error.Code);
    }
}