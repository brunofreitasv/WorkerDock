using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Infrastructure.Persistence;
namespace WorkerDock.IntegrationTests;
public sealed class MigrationTests
{
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

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Upgrade_creates_copy_only_backup_and_verifies_it()
    {
        var backupDirectory = Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_BACKUP_UNC");
        if (string.IsNullOrWhiteSpace(backupDirectory)) return;
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_bootstrap", default);
        var packageRoot = CreateUpgradePackage(database.ScriptsRoot, 1, 10);
        var backup = Path.Combine(backupDirectory, "WorkerDock_Test_" + Guid.NewGuid().ToString("N") + ".bak");
        try
        {
            var upgrade = new MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(packageRoot, "tools", "database-migrator", "database"));
            await upgrade.PrepareUpgradeAsync("tenant", "0004_templates", backup, default);
            Assert.True(new FileInfo(backup).Length > 0);
            var escaped = backup.Replace("'", "''");
            await database.ExecuteAsync($"RESTORE VERIFYONLY FROM DISK=N'{escaped}' WITH CHECKSUM");
            Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM msdb.dbo.backupset s JOIN msdb.dbo.backupmediafamily m ON s.media_set_id=m.media_set_id WHERE m.physical_device_name=N'{escaped}' AND s.is_copy_only=1 AND s.has_backup_checksums=1"));
            Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].SchemaHistory"));
        }
        finally
        {
            if (File.Exists(backup)) File.Delete(backup);
            Directory.Delete(packageRoot, true);
        }
    }

    [Theory]
    [InlineData("DELETE FROM [workerdock].SchemaHistory WHERE MigrationId='0001_bootstrap'", 1, 10, "InvalidMigrationHistory")]
    [InlineData("UPDATE [workerdock].SchemaHistory SET MigrationId='9999_unknown' WHERE MigrationId='0002_quartz'", 1, 10, "InvalidMigrationHistory")]
    [InlineData("UPDATE [workerdock].SchemaHistory SET UpChecksum=REPLICATE('0',64)", 1, 10, "MigrationChecksumMismatch")]
    [InlineData("UPDATE [workerdock].SchemaHistory SET DownChecksum=REPLICATE('0',64)", 1, 10, "MigrationChecksumMismatch")]
    [InlineData("DELETE FROM [workerdock].SchemaHistory", 1, 10, "InvalidMigrationHistory")]
    [InlineData("", 3, 10, "UnsupportedSchemaVersion")]
    [Trait("Store", "Sql")]
    public async Task Upgrade_refuses_invalid_history_or_current_schema_before_backup(string sql, int minimum, int maximum, string code)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0002_quartz", default);
        if (sql.Length > 0) await database.ExecuteAsync(sql);
        var packageRoot = CreateUpgradePackage(database.ScriptsRoot, minimum, maximum);
        try
        {
            var upgrade = new MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(packageRoot, "tools", "database-migrator", "database"));
            var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await upgrade.PrepareUpgradeAsync("tenant", "0004_templates", @"\\localhost\WorkerDockMissingShare\test.bak", default));
            Assert.Equal(code, error.Code);
        }
        finally { Directory.Delete(packageRoot, true); }
    }

    internal static string CreateUpgradePackage(string scriptsRoot, int minimum, int maximum)
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkerDock_Upgrade_" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "tools", "database-migrator", "database", "tenant");
        Directory.CreateDirectory(scripts);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(scriptsRoot, "tenant")))
            File.Copy(file, Path.Combine(scripts, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(root, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new { schemaCompatibility = new { minimum, maximum } }));
        return root;
    }
}
