using WorkerDock.Application.Common;
using WorkerDock.Domain.Tenants;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Infrastructure.Persistence;
namespace WorkerDock.IntegrationTests;
public sealed class TenantSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Audit_failure_rolls_back_setting(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot).ApplyAsync("tenant", "0001_bootstrap", default);
        var service = new TenantSettingsService(new(database.ConnectionString, database.InstanceId));
        var original = await service.GetAsync(default);
        await database.ExecuteAsync("CREATE TRIGGER [workerdock].TestAuditFailure ON [workerdock].AuditEvents AFTER INSERT AS THROW 51000, 'Injected audit failure', 1;");
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(original with { DisplayName = "Changed" }, original.Version, new("issuer|subject", "Entra", Guid.NewGuid()), default));
        Assert.Equal("PersistenceUnavailable", error.Code);
        var reloaded = await service.GetAsync(default);
        Assert.Equal(original.DisplayName, reloaded.DisplayName);
        Assert.Equal(original.Version, reloaded.Version);
        Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AuditEvents;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Store", "Sql")]
    public async Task Update_is_audited_and_stale_version_is_rejected(bool snapshot)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot).ApplyAsync("tenant", "0001_bootstrap", default);
        var service = new TenantSettingsService(new(database.ConnectionString, database.InstanceId));
        var original = await service.GetAsync(default);
        var updated = await service.UpdateAsync(original with { DisplayName = "Equipe", DisplayTimeZone = "America/Sao_Paulo" },
            original.Version, new("session:opaque", "Anonymous", Guid.NewGuid()), default);
        Assert.Equal("Equipe", updated.DisplayName);
        Assert.NotEqual(original.Version, updated.Version);
        var stale = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(original, original.Version, new("session:opaque", "Anonymous", Guid.NewGuid()), default));
        Assert.Equal("StaleVersion", stale.Code);
        Assert.Equal(1, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventType = N'TenantSettingsUpdated' AND DATEPART(TZOFFSET, OccurredAtUtc AT TIME ZONE 'UTC') = 0;"));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => database.ExecuteAsync("UPDATE [workerdock].AuditEvents SET EventType=N'Changed';"));
    }

    // Removing pre-connection validation would turn this into a driver/network failure.
    [Fact]
    public async Task Invalid_settings_and_actor_are_rejected_before_connecting()
    {
        var id = Guid.NewGuid();
        var service = new TenantSettingsService(new("invalid connection", id));
        var desired = new TenantSettings(id, "", "UTC", new byte[8]);
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(desired, new byte[8], new("issuer|subject", "Entra", Guid.NewGuid()), default));
        Assert.Equal("ValidationFailed", error.Code);
        Assert.Equal("displayName", error.Field);
        error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(desired with { DisplayName = "Valid" }, new byte[8], new("", "Anonymous", Guid.NewGuid()), default));
        Assert.Equal("actor", error.Field);
    }
}
