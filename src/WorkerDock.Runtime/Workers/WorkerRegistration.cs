using WorkerDock.Application.Workers;
namespace WorkerDock.Runtime.Workers;

public sealed class WorkerRegistration(ICapacityStore store)
{
    public async ValueTask RegisterAsync(Guid installationId, string bootId, string catalogDigest, int ceiling, CancellationToken ct) =>
        await store.TransactAsync((s, now) => { s.Register(installationId, bootId, catalogDigest, ceiling, now); return true; }, ct);
}
