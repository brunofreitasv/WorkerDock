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
        Assert.True(await database.ScalarAsync("SELECT COUNT(*) FROM sys.schemas WHERE name IN(N'db_owner',N'db_datareader',N'db_datawriter')") >= 3);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.RuntimePrincipal, database.ScriptsRoot);
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

        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.RuntimePrincipal, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory WHERE MigrationId=N'0001_initial'"));
        var wrong = new MigrationRunner(database.ConnectionString, Guid.NewGuid(), database.RuntimePrincipal, database.ScriptsRoot);
        var wrongInstance = await Assert.ThrowsAsync<WorkerDockException>(async () => await wrong.ApplyAsync("tenant", "0001_initial", default));
        Assert.Equal("TenantInstanceMismatch", wrongInstance.Code);
        await database.ExecuteAsync("UPDATE [workerdock].SchemaHistory SET UpChecksum=REPLICATE('0',64)");
        var tampered = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_initial", default));
        Assert.Equal("MigrationChecksumMismatch", tampered.Code);
    }

    [Fact]
    public async Task Unknown_target_is_rejected_before_connecting()
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), "test-runtime", Path.GetTempPath());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "missing", default));
        Assert.Equal("UnknownMigration", error.Code);
    }

    [Fact]
    public async Task Invalid_database_kind_fails_before_connecting()
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), "test-runtime", Path.GetTempPath());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("other", "0001_initial", default));
        Assert.Equal("InvalidDatabaseKind", error.Code);
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Migration_rejects_missing_runtime_principal_before_creating_schema()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, "WorkerDock_Missing_Runtime", database.ScriptsRoot);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_initial", default));

        Assert.Equal("RuntimePrincipalInvalid", error.Code);
        Assert.Equal(0, await database.ScalarAsync("SELECT CASE WHEN SCHEMA_ID(N'workerdock') IS NULL THEN 0 ELSE 1 END"));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Migration_rejects_role_as_runtime_principal_before_creating_schema()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ExecuteAsync("CREATE ROLE [WorkerDock_Test_Role];");
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, "WorkerDock_Test_Role", database.ScriptsRoot);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_initial", default));

        Assert.Equal("RuntimePrincipalInvalid", error.Code);
        Assert.Equal(0, await database.ScalarAsync("SELECT CASE WHEN SCHEMA_ID(N'workerdock') IS NULL THEN 0 ELSE 1 END"));
    }

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Migration_rejects_nonempty_database_before_creating_schema()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ExecuteAsync("CREATE TABLE dbo.MigrationSentinel (Id int NOT NULL);");
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.RuntimePrincipal, database.ScriptsRoot);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_initial", default));

        Assert.Equal("TenantDatabaseNotEmpty", error.Code);
        Assert.Equal(0, await database.ScalarAsync("SELECT CASE WHEN SCHEMA_ID(N'workerdock') IS NULL THEN 0 ELSE 1 END"));
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.MigrationSentinel')"));
    }
}
