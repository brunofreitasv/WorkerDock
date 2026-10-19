using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Quartz;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Quartz.Hosting;

namespace WorkerDock.IntegrationTests;

public sealed class CustomSqlSchemaTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkerDock.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root missing");
        }
    }

    [Theory]
    [InlineData("WorkerDock.Infrastructure.Persistence.TenantSettingsDbContext", "TenantSettings")]
    [InlineData("WorkerDock.Infrastructure.Jobs.JobDbContext", "Jobs")]
    [InlineData("WorkerDock.Infrastructure.Jobs.JobDbContext", "JobRevisions")]
    [InlineData("WorkerDock.Infrastructure.Jobs.JobDbContext", "Commands")]
    public void Ef_relational_model_maps_each_tenant_table_to_custom_schema(string contextName, string tableName)
    {
        var contextType = typeof(TenantSettingsService).Assembly.GetType(contextName, throwOnError: true)!;
        using var context = (DbContext)Activator.CreateInstance(contextType, "Server=unused")!;
        var table = Assert.Single(context.Model.GetRelationalModel().Tables, table => table.Name == tableName);
        Assert.Equal("workerdock", table.Schema);
    }

    [Fact]
    public void Bootstrap_owns_schema_lifetime_and_refuses_dependent_rollbacks()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "tenant");
        var up = File.ReadAllText(Path.Combine(directory, "0001_bootstrap.up.sql"));
        var down = File.ReadAllText(Path.Combine(directory, "0001_bootstrap.down.sql"));
        Assert.True(up.IndexOf("CREATE SCHEMA [workerdock]", StringComparison.Ordinal) >= 0);
        Assert.True(up.IndexOf("CREATE SCHEMA [workerdock]", StringComparison.Ordinal) < up.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        Assert.Contains("MigrationId <> N'0001_bootstrap'", down);
        Assert.EndsWith("DROP SCHEMA [workerdock];", down.Trim());
    }

    [Fact]
    public void Every_paired_migration_targets_custom_schema_and_matches_exact_manifest_bytes()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "tenant");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var ids = manifest.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(new[] { "0001_bootstrap", "0002_quartz", "0003_execution", "0003a_projection", "0003b_attempts", "0003c_capacity", "0003d_dispatch", "0004_templates", "0004a_profiles", "0004b_integration_settings", "0005_scheduling", "0006_observability" }, ids);
        Assert.Equal(ids.Length * 2, Directory.GetFiles(directory, "*.sql").Length);
        foreach (var entry in manifest.RootElement.EnumerateArray())
        foreach (var direction in new[] { "up", "down" })
        {
            var path = Path.Combine(directory, entry.GetProperty("id").GetString() + "." + direction + ".sql");
            var sql = File.ReadAllText(path);
            Assert.False(Regex.IsMatch(sql, @"\b dbo\s*\.|\[dbo\]\s*\.", RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase), path);
            Assert.Contains("[workerdock]", sql);
            Assert.Equal(entry.GetProperty(direction + "Sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            foreach (Match table in Regex.Matches(sql, @"(?im)^CREATE (?:TABLE|TRIGGER)\s+(\S+)"))
                Assert.StartsWith("[workerdock].", table.Groups[1].Value);
        }
    }

    [Fact]
    public void Runtime_and_probe_sql_never_resolve_tenant_objects_via_default_schema()
    {
        var tables = Directory.GetFiles(Path.Combine(Root, "database", "tenant"), "*.up.sql")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"(?im)^CREATE TABLE\s+(?:\[?\w+\]?\.)\[?(\w+)\]?")
                .Select(match => match.Groups[1].Value)).Concat(new[] { "QuartzProbeState", "QuartzProbeEvents" }).Distinct().ToArray();
        foreach (var area in new[] { "src", "tests", Path.Combine("database", "tenant") })
        foreach (var path in Directory.GetFiles(Path.Combine(Root, area), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => Path.GetExtension(path) is ".cs" or ".sql"))
        {
            if (path.EndsWith(nameof(CustomSqlSchemaTests) + ".cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(path);
            Assert.False(Regex.IsMatch(text, @"\bdbo\s*\.|\[dbo\]\s*\.", RegexOptions.IgnoreCase), path);
            foreach (var table in tables)
                Assert.False(Regex.IsMatch(text, @"\b(?:FROM|JOIN|INTO|UPDATE|TABLE|REFERENCES|ON)\s+\[?" + Regex.Escape(table) + @"\]?\b", RegexOptions.IgnoreCase), path + ": " + table);
        }
    }

    [Theory]
    [InlineData(false, "WDS_QRTZ_")]
    [InlineData(true, "WDE_QRTZ_")]
    public void Quartz_runtime_prefix_uses_public_schema_qualified_configuration(bool execution, string suffix)
    {
        var source = File.ReadAllText(Path.Combine(Root, "src", "WorkerDock.Quartz", "Hosting", "QuartzTopology.cs"));
        var prefix = "[workerdock]." + suffix;
        Assert.Contains("\"" + prefix + "\"", source);
        var options = new AdoJobStoreOptions();
        QuartzTopology.ConfigurePersistentStore(options, execution);
        Assert.Equal(prefix, options.TablePrefix);
        Assert.Equal(SchemaProvisioning.Validate, options.SchemaProvisioning);
        Assert.True(options.AcquireTriggersWithinLock);
        Assert.True(options.StoreJobDataAsStrings);
        Assert.Contains("settings.SchemaProvisioning = SchemaProvisioning.Validate", source);
    }
}
