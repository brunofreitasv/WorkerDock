using WorkerDock.Application.Common;
using WorkerDock.Application.Tenants;
using WorkerDock.Domain.Tenants;

namespace WorkerDock.Infrastructure.Persistence;

public sealed class InMemoryTenantSettingsService(Guid instanceId, Func<ActorContext, CancellationToken, ValueTask>? appendAudit = null) : ITenantSettingsService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private TenantSettings current = new(instanceId, "WorkerDock", "UTC", BitConverter.GetBytes(1L));
    private int auditEventCount;
    public int AuditEventCount => Volatile.Read(ref auditEventCount);

    public async ValueTask<TenantSettings> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return current with { Version = (byte[])current.Version.Clone() }; }
        finally { gate.Release(); }
    }

    public async ValueTask<TenantSettings> UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        TenantSettingsValidation.Validate(desired, expectedVersion, actor, instanceId);
        await gate.WaitAsync(ct);
        try
        {
            if (!current.Version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
            var next = desired with { Version = BitConverter.GetBytes(BitConverter.ToInt64(current.Version) + 1) };
            try
            {
                if (appendAudit is not null) await appendAudit(actor, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw new WorkerDockException("PersistenceUnavailable"); }
            current = next;
            Interlocked.Increment(ref auditEventCount);
            return next with { Version = (byte[])next.Version.Clone() };
        }
        finally { gate.Release(); }
    }
}
