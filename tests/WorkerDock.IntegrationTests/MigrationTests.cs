using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Infrastructure.Persistence;
namespace WorkerDock.IntegrationTests;
public sealed class MigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
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
        await database.ExecuteAsync("UPDATE dbo.SchemaHistory SET UpChecksum = REPLICATE('0',64);");
        var tampered = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_bootstrap", default));
        Assert.Equal("MigrationChecksumMismatch", tampered.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_down_up_preserves_schema_contract(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_bootstrap", default);
        Assert.Equal(4, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN ('TenantSettings','AuditEvents','SchemaHistory','Outbox');"));
        await runner.RollbackAsync("tenant", "0001_bootstrap", default);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.tables WHERE name IN ('TenantSettings','AuditEvents','SchemaHistory','Outbox');"));
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
