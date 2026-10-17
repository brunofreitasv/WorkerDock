using WorkerDock.Application.Common;
using WorkerDock.Domain.Tenants;
namespace WorkerDock.Application.Tenants;
public interface ITenantSettingsService
{
    ValueTask<TenantSettings> GetAsync(CancellationToken ct);
    ValueTask<TenantSettings> UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
}
