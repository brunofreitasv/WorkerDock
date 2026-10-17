using WorkerDock.Application.Common;
using WorkerDock.Domain.Tenants;

namespace WorkerDock.Application.Tenants;

public static class TenantSettingsValidation
{
    public static void Validate(TenantSettings desired, byte[] expectedVersion, ActorContext actor, Guid instanceId)
    {
        if (desired.InstanceId != instanceId) throw new WorkerDockException("TenantInstanceMismatch");
        if (string.IsNullOrWhiteSpace(desired.DisplayName) || desired.DisplayName.Length > 200)
            throw new WorkerDockException("ValidationFailed", "displayName");
        if (string.IsNullOrWhiteSpace(desired.DisplayTimeZone) || desired.DisplayTimeZone.Length > 100)
            throw new WorkerDockException("ValidationFailed", "displayTimeZone");
        if (expectedVersion is null || expectedVersion.Length != 8)
            throw new WorkerDockException("ValidationFailed", "version");
        if (actor is null || string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 300 ||
            string.IsNullOrWhiteSpace(actor.AuthenticationMode) || actor.AuthenticationMode.Length > 40 || actor.CorrelationId == Guid.Empty)
            throw new WorkerDockException("ValidationFailed", "actor");
    }
}
