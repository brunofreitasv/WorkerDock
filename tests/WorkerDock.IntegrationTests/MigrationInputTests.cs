using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;
using System.Security.Cryptography;
using System.Text.Json;
namespace WorkerDock.IntegrationTests;
public sealed class MigrationInputTests
{
    [Fact]
    public void Tenant_manifest_checksums_match_every_up_and_down_script()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "tenant");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString()!;
            foreach (var direction in new[] { "up", "down" })
            {
                var script = Path.Combine(directory, $"{id}.{direction}.sql");
                var expected = entry.GetProperty(direction + "Sha256").GetString();
                Assert.True(File.Exists(script), $"Missing {id}.{direction}.sql");
                Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(script))));
            }
        }
    }

    [Fact]
    public async Task Modified_script_is_rejected_before_connecting()
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkerDock_Manifest_" + Guid.NewGuid().ToString("N"));
        var tenant = Path.Combine(root, "tenant");
        Directory.CreateDirectory(tenant);
        try
        {
            foreach (var name in new[] { "manifest.json", "0001_bootstrap.up.sql", "0001_bootstrap.down.sql" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "database", "tenant", name), Path.Combine(tenant, name));
            await File.AppendAllTextAsync(Path.Combine(tenant, "0001_bootstrap.up.sql"), "\n-- modified\n");
            var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), root);
            var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "0001_bootstrap", default));
            Assert.Equal("MigrationChecksumMismatch", error.Code);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(tenant)) File.Delete(file);
            Directory.Delete(tenant);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Unknown_target_is_rejected_before_connecting()
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.Combine(AppContext.BaseDirectory, "database"));
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("tenant", "missing", default));
        Assert.Equal("UnknownMigration", error.Code);
    }

    [Fact]
    public async Task Central_database_kind_is_rejected_before_connecting()
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.Combine(AppContext.BaseDirectory, "database"));
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.ApplyAsync("central", "0001_registry", default));
        Assert.Equal("InvalidDatabaseKind", error.Code);
    }

    [Theory]
    [InlineData("central", "0001_bootstrap", @"\\localhost\WorkerDockMissingShare\test.bak", "InvalidDatabaseKind")]
    [InlineData("tenant", "missing", @"\\localhost\WorkerDockMissingShare\test.bak", "UnknownMigration")]
    [InlineData("tenant", "0001_bootstrap", "C:\\backup.bak", "InvalidBackupPath")]
    [InlineData("tenant", "0001_bootstrap", @"\\localhost\share\backup.zip", "InvalidBackupPath")]
    [InlineData("tenant", "0001_bootstrap", @"\\?\C:\backup.bak", "InvalidBackupPath")]
    [InlineData("tenant", "0001_bootstrap", @"\\localhost\share\..\backup.bak", "InvalidBackupPath")]
    public async Task Upgrade_input_is_rejected_before_connecting(string kind, string target, string path, string code)
    {
        var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.Combine(AppContext.BaseDirectory, "database"));
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.PrepareUpgradeAsync(kind, target, path, default));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Upgrade_existing_backup_is_rejected_before_connecting_and_preserved()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bak");
        await File.WriteAllTextAsync(path, "existing-backup");
        try
        {
            var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.Combine(AppContext.BaseDirectory, "database"));
            var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.PrepareUpgradeAsync("tenant", "0001_bootstrap", path, default));
            Assert.Equal("BackupAlreadyExists", error.Code);
            Assert.Equal("existing-backup", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("SENTINEL-legacy-password")]
    public async Task Migrator_rejects_present_legacy_environment_even_when_empty(string legacy)
    {
        var result = await RunMigratorAsync(new() { ["WORKERDOCK_SQL"] = legacy });
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("LegacyConnectionStringUnsupported", result.Error.Trim());
        Assert.DoesNotContain("SENTINEL", result.Output + result.Error);
    }

    [Fact]
    public async Task Migrator_rejects_missing_sql_configuration_without_sql_access()
    {
        var result = await RunMigratorAsync(new());
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("SqlConfigurationInvalid", result.Error.Trim());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Migrator_resolves_environment_or_json_reference_and_sanitizes_unavailability(bool jsonConfig, bool production)
    {
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_ENVIRONMENT"] = production ? "Production" : "Development",
            ["WORKERDOCK_DEVELOPMENT_VAULT"] = "1",
            ["WORKERDOCK_DEVELOPMENT_SECRET_MAP"] = production ? "{\"migration-password\":\"SENTINEL_SECRET_ENV\"}" : "{}",
            ["SENTINEL_SECRET_ENV"] = "SENTINEL-password"
        };
        var leaves = new Dictionary<string, string>
        {
            ["Host"] = "localhost", ["Port"] = "1433", ["Database"] = "unused",
            ["User"] = "migrator", ["PasswordReference"] = "migration-password"
        };
        string? path = null;
        if (jsonConfig)
        {
            path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Migrator = new { Sql = leaves } }));
            environment["WORKERDOCK_MIGRATOR_CONFIG_PATH"] = path;
        }
        else foreach (var (key, value) in leaves) environment["Migrator__Sql__" + key] = value;
        try
        {
            var result = await RunMigratorAsync(environment);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal("SecretUnavailable", result.Error.Trim());
            Assert.DoesNotContain("SENTINEL", result.Output + result.Error);
        }
        finally { if (path is not null) File.Delete(path); }
    }


    [Fact]
    public async Task Upgrade_rejects_target_outside_package_compatibility_before_connecting()
    {
        var root = MigrationTests.CreateUpgradePackage(Path.Combine(AppContext.BaseDirectory, "database"), 2, 10);
        try
        {
            var runner = new MigrationRunner("invalid connection", Guid.NewGuid(), Path.Combine(root, "tools", "database-migrator", "database"));
            var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.PrepareUpgradeAsync("tenant", "0001_bootstrap", @"\\localhost\WorkerDockMissingShare\test.bak", default));
            Assert.Equal("UnsupportedSchemaVersion", error.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunMigratorAsync(Dictionary<string, string> environment)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("WORKERDOCK_", StringComparison.OrdinalIgnoreCase) ||
                     key.StartsWith("Migrator", StringComparison.OrdinalIgnoreCase) || key is "DOTNET_ENVIRONMENT" or "ASPNETCORE_ENVIRONMENT").ToArray())
            start.Environment.Remove(key);
        start.Environment["WORKERDOCK_INSTANCE_ID"] = Guid.NewGuid().ToString();
        foreach (var (key, value) in environment) start.Environment[key] = value;
        start.ArgumentList.Add(typeof(MigrationRunner).Assembly.Location);
        foreach (var argument in new[] { "apply", "tenant", "0001_bootstrap" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        return (process.ExitCode, await output, await error);
    }
}
