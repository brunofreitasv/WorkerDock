using System.Collections.Immutable;
using WorkerDock.Application.Common;
using WorkerDock.Application.Tenants;
using WorkerDock.Domain.Tenants;

namespace WorkerDock.Infrastructure.Persistence;

public sealed class InMemoryTenantSettingsService(Guid instanceId, Func<CancellationToken, ValueTask>? beforeAuditCommit = null) : ITenantSettingsService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private MemoryState state = new(new(instanceId, "WorkerDock", "UTC", BitConverter.GetBytes(1L)), []);
    public int AuditEventCount => Volatile.Read(ref state).AuditEvents.Length;
    public ImmutableArray<TenantAuditEvent> AuditEvents => Volatile.Read(ref state).AuditEvents;

    public async ValueTask<TenantSettings> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = state.Settings;
            return current with { Version = (byte[])current.Version.Clone() };
        }
        finally { gate.Release(); }
    }

    public async ValueTask<TenantSettings> UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        TenantSettingsValidation.Validate(desired, expectedVersion, actor, instanceId);
        await gate.WaitAsync(ct);
        try
        {
            var snapshot = state;
            var current = snapshot.Settings;
            if (!current.Version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
            var next = desired with { Version = BitConverter.GetBytes(BitConverter.ToInt64(current.Version) + 1) };
            try
            {
                if (beforeAuditCommit is not null) await beforeAuditCommit(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw new WorkerDockException("PersistenceUnavailable"); }
            ct.ThrowIfCancellationRequested();
            var audit = new TenantAuditEvent(Guid.NewGuid(), "TenantSettingsUpdated", actor.Subject,
                actor.AuthenticationMode, actor.CorrelationId, DateTimeOffset.UtcNow);
            Volatile.Write(ref state, new MemoryState(next, snapshot.AuditEvents.Add(audit)));
            return next with { Version = (byte[])next.Version.Clone() };
        }
        finally { gate.Release(); }
    }

    private sealed record MemoryState(TenantSettings Settings, ImmutableArray<TenantAuditEvent> AuditEvents);
}

public sealed record TenantAuditEvent(Guid EventId, string EventType, string ActorSubject,
    string AuthenticationMode, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
