using WorkerDock.Application.Common;
using WorkerDock.DatabaseMigrator;
namespace WorkerDock.IntegrationTests;
public sealed class MigrationInputTests
{
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
}
