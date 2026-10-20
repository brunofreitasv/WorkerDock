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
}
