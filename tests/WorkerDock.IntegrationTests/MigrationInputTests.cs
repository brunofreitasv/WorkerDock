using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Migrations;
using System.Security.Cryptography;
using System.Text.Json;
namespace WorkerDock.IntegrationTests;
public sealed class MigrationInputTests
{
    [Fact]
    public void Upgrade_guide_documents_builtin_template_migration_preflight_and_recovery()
    {
        var guide = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "how-to", "upgrade-and-restore.md"));
        Assert.Contains("0010_remove_builtin_v1", guide, StringComparison.Ordinal);
        Assert.Contains("execution refers to a built-in 1.0.0 revision", guide, StringComparison.Ordinal);
        Assert.Contains("verified pre-upgrade database backup", guide, StringComparison.Ordinal);
        Assert.Contains("stop the tenant Web, scheduler, and Worker services", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_database_guides_document_manual_migration_and_fresh_snapshot_paths()
    {
        var root = FindRepositoryRoot();
        var setup = File.ReadAllText(Path.Combine(root, "docs", "how-to", "setup-sql-server.md"));
        var tutorial = File.ReadAllText(Path.Combine(root, "docs", "tutorials", "run-with-sql-server.md"));
        var upgrade = File.ReadAllText(Path.Combine(root, "docs", "how-to", "upgrade-and-restore.md"));
        Assert.Contains("scripts/Invoke-TenantMigration.ps1", setup, StringComparison.Ordinal);
        Assert.Contains("Operator chooses a preparation path", setup, StringComparison.Ordinal);
        Assert.Contains("database/tenant/current-state.sql", setup, StringComparison.Ordinal);
        Assert.Contains("fresh-install-only", setup, StringComparison.Ordinal);
        Assert.Contains("__WORKERDOCK_INSTANCE_ID__", setup, StringComparison.Ordinal);
        Assert.Contains("same snapshot action", setup, StringComparison.Ordinal);
        Assert.Contains("one SQL batch", setup, StringComparison.Ordinal);
        Assert.Contains("equivalent satisfied state", setup, StringComparison.Ordinal);
        Assert.Contains("& scripts/Invoke-TenantMigration.ps1", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh -NoProfile -File scripts/Invoke-TenantMigration.ps1", setup, StringComparison.Ordinal);
        Assert.Contains("PowerShell host must run on .NET 10 or later", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("Deployment-only migrator", setup, StringComparison.Ordinal);
        Assert.Contains("database/tenant/current-state.sql", tutorial, StringComparison.Ordinal);
        Assert.Contains("& scripts/Invoke-TenantMigration.ps1", tutorial, StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh -NoProfile -File scripts/Invoke-TenantMigration.ps1", tutorial, StringComparison.Ordinal);
        Assert.Contains("operator-managed SQL Server backup", upgrade, StringComparison.Ordinal);
        Assert.DoesNotContain("package manifest", upgrade, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schema compatibility window", upgrade, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release compatibility evidence", upgrade, StringComparison.Ordinal);
        Assert.Contains("scripts/Invoke-TenantMigration.ps1", upgrade, StringComparison.Ordinal);
        Assert.True(upgrade.IndexOf("operator-managed SQL Server backup", StringComparison.Ordinal) <
                    upgrade.IndexOf("scripts/Invoke-TenantMigration.ps1", StringComparison.Ordinal));
    }

    [Fact]
    public void Deployment_reference_does_not_claim_optional_windows_or_package_hash_smoke_checks()
    {
        var reference = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "reference", "deployment-model.md"));
        var normalized = string.Join(" ", reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Worker and Executor currently require Windows", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows/IIS hosting is an optional deployment example", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("package-manifest hash handling", reference, StringComparison.Ordinal);
        var troubleshooting = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "how-to", "troubleshooting.md"));
        Assert.DoesNotContain("package-manifest integrity handling", troubleshooting, StringComparison.Ordinal);
        Assert.Contains("runtime secret-reference examples", troubleshooting, StringComparison.Ordinal);
    }

    [Fact]
    public void Troubleshooting_guide_documents_manual_interrupted_deployment_recovery()
    {
        var guide = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "how-to", "troubleshooting.md"));
        Assert.DoesNotContain("The upgrade script does not auto-resume", guide, StringComparison.Ordinal);
        Assert.Contains("Manually compare service and binary versions", guide, StringComparison.Ordinal);
        Assert.Contains("verified backups", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_guides_do_not_refer_to_removed_deployment_lifecycle_tools()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "docs", "how-to"), "*.md")
            .Concat(Directory.GetFiles(Path.Combine(root, "docs", "reference"), "*.md"))
            .Concat(Directory.GetFiles(Path.Combine(root, "docs", "tutorials"), "*.md"))
            .Append(Path.Combine(root, "README.md"));
        foreach (var file in files)
        {
            var guide = File.ReadAllText(file);
            Assert.DoesNotContain("Upgrade-Instance.ps1", guide, StringComparison.Ordinal);
            Assert.DoesNotContain("Build-Packages.ps1", guide, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkerDock.DatabaseMigrator", guide, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "docs")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the WorkerDock repository root.");
    }

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
