namespace WorkerDock.Domain.Tenants;
public sealed record TenantSettings(Guid InstanceId, string DisplayName, string DisplayTimeZone, byte[] Version);
