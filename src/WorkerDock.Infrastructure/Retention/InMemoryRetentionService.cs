using WorkerDock.Application.Common;
using WorkerDock.Application.Retention;

namespace WorkerDock.Infrastructure.Retention;

/// <summary>Development-only retention settings adapter; it never removes operational records.</summary>
public sealed class InMemoryRetentionService : IRetentionService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RetentionPolicy policy = RetentionPolicy.Default;
    private byte[] version = BitConverter.GetBytes(1L);

    public RetentionPolicy Policy => Volatile.Read(ref policy);

    public ValueTask<int> RunBatchAsync(DateTimeOffset nowUtc, int maxRows, CancellationToken ct)
    {
        if (maxRows is < 1 or > 1000) throw new WorkerDockException("ValidationFailed");
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(0);
    }

    public async ValueTask<RetentionPolicyEditorDto> GetPolicyAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return new(policy, (byte[])version.Clone()); }
        finally { gate.Release(); }
    }

    public async ValueTask<CommandReceipt> SavePolicyAsync(RetentionPolicy desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        try { RetentionPolicyValidation.Validate(desired); }
        catch (ArgumentException) { throw new WorkerDockException("ValidationFailed", "policy"); }
        if (expectedVersion is not { Length: 8 } || actor is null || string.IsNullOrWhiteSpace(actor.Subject) ||
            string.IsNullOrWhiteSpace(actor.AuthenticationMode) || actor.CorrelationId == Guid.Empty)
            throw new WorkerDockException("ValidationFailed");

        await gate.WaitAsync(ct);
        try
        {
            if (!version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
            policy = desired;
            version = BitConverter.GetBytes(checked(BitConverter.ToInt64(version) + 1));
            return new(Guid.NewGuid(), RetentionPolicy.TargetId, "Applied");
        }
        finally { gate.Release(); }
    }
}
