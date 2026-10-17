using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

public sealed class InMemoryTenantSettingsTests
{
    [Fact]
    public async Task Update_changes_settings_and_appends_audit()
    {
        var service = new InMemoryTenantSettingsService(Guid.NewGuid());
        var original = await service.GetAsync(default);
        var updated = await service.UpdateAsync(original with { DisplayName = "Equipe", DisplayTimeZone = "America/Sao_Paulo" },
            original.Version, new("issuer|subject", "Entra", Guid.NewGuid()), default);
        Assert.Equal("Equipe", (await service.GetAsync(default)).DisplayName);
        Assert.Equal("America/Sao_Paulo", updated.DisplayTimeZone);
        Assert.NotEqual(original.Version, updated.Version);
        Assert.Single(service.AuditEvents);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_settings_and_version()
    {
        var service = new InMemoryTenantSettingsService(Guid.NewGuid(), _ => throw new InvalidOperationException("audit unavailable"));
        var original = await service.GetAsync(default);
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(original with { DisplayName = "Changed" }, original.Version,
                new("issuer|subject", "Entra", Guid.NewGuid()), default));
        Assert.Equal("PersistenceUnavailable", error.Code);
        var reloaded = await service.GetAsync(default);
        Assert.Equal(original.DisplayName, reloaded.DisplayName);
        Assert.Equal(original.Version, reloaded.Version);
        Assert.Empty(service.AuditEvents);
    }

    [Fact]
    public async Task Stale_version_and_wrong_instance_are_rejected_without_audit()
    {
        var service = new InMemoryTenantSettingsService(Guid.NewGuid());
        var original = await service.GetAsync(default);
        var actor = new ActorContext("issuer|subject", "Entra", Guid.NewGuid());
        var wrong = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(original with { InstanceId = Guid.NewGuid() }, original.Version, actor, default));
        Assert.Equal("TenantInstanceMismatch", wrong.Code);
        var stale = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.UpdateAsync(original, new byte[8], actor, default));
        Assert.Equal("StaleVersion", stale.Code);
        Assert.Empty(service.AuditEvents);
    }

    [Fact]
    public async Task Default_memory_adapter_keeps_audit_events()
    {
        var service = new InMemoryTenantSettingsService(Guid.NewGuid());
        var original = await service.GetAsync(default);
        var correlation = Guid.NewGuid();
        await service.UpdateAsync(original with { DisplayName = "Team" }, original.Version,
            new("issuer|subject", "Entra", correlation), default);
        Assert.Equal(1, service.AuditEventCount);
        var audit = Assert.Single(service.AuditEvents);
        Assert.Equal("TenantSettingsUpdated", audit.EventType);
        Assert.Equal("issuer|subject", audit.ActorSubject);
        Assert.Equal("Entra", audit.AuthenticationMode);
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.NotEqual(Guid.Empty, audit.EventId);
        Assert.InRange(audit.OccurredAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
    }
}
