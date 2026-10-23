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
        Assert.Contains("0013_operational_retention", ids);
        Assert.Equal(ids.Length, Directory.GetFiles(directory, "*.up.sql").Length);
        Assert.Equal(ids.Length, Directory.GetFiles(directory, "*.down.sql").Length);
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
    public void Retention_schema_keeps_replay_tombstones_and_restricts_audit_purge_to_a_bounded_procedure()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "tenant");
        var up = File.ReadAllText(Path.Combine(directory, "0008_retention.up.sql"));
        var down = File.ReadAllText(Path.Combine(directory, "0008_retention.down.sql"));
        Assert.Contains("RetentionPolicy", up);
        Assert.Contains("Version rowversion NOT NULL", up);
        Assert.Contains("VALUES(1,30,90,365,0,0)", up);
        Assert.Contains("ArchiveBatches", up);
        Assert.Contains("ArchiveRemovedAtUtc", up);
        Assert.Contains("CREATE PROCEDURE [workerdock].PurgeExpiredArchiveBatchItems", up);
        Assert.Contains("DENY DELETE ON [workerdock].ArchiveBatchItems TO WorkerDockRuntime", up);
        Assert.Contains("LogCutoffUtc datetime2(7) NOT NULL", up);
        Assert.Contains("LogCutoffUtc=@LogCutoffUtc", up);
        Assert.Contains("AggregatedAtUtc IS NOT NULL", up);
        Assert.Contains("N'Abandoned'", up);
        Assert.Contains("Archive log item is newer than the batch log cutoff", up);
        Assert.Contains("RetentionRequestTombstones", up);
        Assert.Contains("RetentionPurgeAudit", up);
        Assert.Contains("CREATE PROCEDURE [workerdock].PurgeAuditEvents", up);
        Assert.Contains("CREATE PROCEDURE [workerdock].PurgeRetentionRequestTombstones", up);
        Assert.Contains("EXECUTE AS OWNER", up);
        Assert.Contains("DELETE TOP (@MaxRows)", up);
        Assert.Contains("GRANT EXECUTE ON [workerdock].PurgeAuditEvents TO WorkerDockRuntime", up);
        Assert.DoesNotContain("GRANT DELETE ON [workerdock].AuditEvents TO WorkerDockRuntime", up);
        Assert.DoesNotContain("GRANT SELECT,INSERT,UPDATE,DELETE ON [workerdock].RetentionRequestTombstones TO WorkerDockRuntime", up);
        Assert.Contains("Cannot remove populated retention state", down);
    }

    [Fact]
    public void Operational_retention_transition_consolidates_event_and_policy_state()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "tenant");
        var up = File.ReadAllText(Path.Combine(directory, "0013_operational_retention.up.sql"));
        var down = File.ReadAllText(Path.Combine(directory, "0013_operational_retention.down.sql"));

        Assert.Contains("CREATE TABLE [workerdock].OperationalEvents", up);
        Assert.Contains("PurgeOperationalEvents", up);
        Assert.Contains("LastEvaluatedAtUtc", up);
        Assert.Contains("ArchiveBeforeDelete", up);
        Assert.Contains("AuditEvents", File.ReadAllText(Path.Combine(Root, "src", "WorkerDock.Infrastructure", "Retention", "SqlRetentionService.cs")));
        Assert.Contains("Operational event consolidation cannot be rolled back", down);
        Assert.DoesNotContain("DROP TABLE [workerdock].ArchiveBatches", down);
        Assert.DoesNotContain("DROP TABLE [workerdock].ArchiveBatchItems", down);
        Assert.DoesNotContain("DROP TABLE [workerdock].RetentionRequestTombstones", down);
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
            .Where(path => !path.StartsWith(Path.Combine(Root, "tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path) == ".sql")
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
