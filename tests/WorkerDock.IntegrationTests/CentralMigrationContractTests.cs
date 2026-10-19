using System.Security.Cryptography;
using System.Text.Json;

namespace WorkerDock.IntegrationTests;

public sealed class CentralMigrationContractTests
{
    [Fact]
    public void Central_migration_is_paired_checksummed_and_contains_only_registry_state()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "database", "central");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var entry = Assert.Single(manifest.RootElement.EnumerateArray());
        Assert.Equal("0001_registry", entry.GetProperty("id").GetString());
        foreach (var direction in new[] { "up", "down" })
        {
            var path = Path.Combine(directory, "0001_registry." + direction + ".sql");
            var sql = File.ReadAllText(path);
            Assert.Contains("[workerdock]", sql);
            Assert.DoesNotContain("TenantSettings", sql);
            Assert.DoesNotContain("[workerdock].Jobs", sql);
            Assert.Equal(entry.GetProperty(direction + "Sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        var up = File.ReadAllText(Path.Combine(directory, "0001_registry.up.sql"));
        var down = File.ReadAllText(Path.Combine(directory, "0001_registry.down.sql"));
        Assert.Contains("CentralSettings", up);
        Assert.Contains("TenantRegistrations", up);
        Assert.Contains("TenantHealthObservations", up);
        Assert.Contains("HealthCredentialReference", up);
        Assert.Contains("CentralAuditEvents_AppendOnly", up);
        Assert.Contains("GRANT SELECT, INSERT ON OBJECT::[workerdock].[AuditEvents]", up);
        Assert.DoesNotContain("GRANT UPDATE ON OBJECT::[workerdock].[AuditEvents]", up);
        Assert.Contains("Central registry contains registrations", down);
        Assert.Contains("Central audit contains events", down);
    }
}
