namespace WorkerDock.Infrastructure.Persistence;
public sealed record TenantDatabaseOptions(string ConnectionString, Guid InstanceId);
