using WorkerDock.Application.Common;

namespace WorkerDock.Application.Retention;

public sealed record RetentionPolicy(int LogDays, int ExecutionDays, int AuditDays, bool ArchiveBeforeDelete, int ArchiveDays)
{
    public static Guid TargetId { get; } = new("785c42a4-5bce-4da2-b109-6cb2cd1c2bf6");
    public static RetentionPolicy Default { get; } = new(30, 90, 365, false, 0);
}

public sealed record RetentionPolicyEditorDto(RetentionPolicy Policy, byte[] Version);

public sealed record ArchiveManifest(Guid BatchId, string RelativePath, string Sha256, long RowCount, DateTimeOffset FinalizedUtc);

public interface IRetentionService
{
    ValueTask<int> RunBatchAsync(DateTimeOffset nowUtc, int maxRows, CancellationToken ct);
    ValueTask<RetentionPolicyEditorDto> GetPolicyAsync(CancellationToken ct);
    ValueTask<CommandReceipt> SavePolicyAsync(RetentionPolicy policy, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
}

public static class RetentionPolicyValidation
{
    public static void Validate(RetentionPolicy policy)
    {
        if (policy is null || policy.LogDays is < 1 or > 3650 || policy.ExecutionDays is < 1 or > 3650 ||
            policy.AuditDays is < 1 or > 3650 || policy.ArchiveDays is < 0 or > 3650 ||
            policy.ArchiveBeforeDelete && policy.ArchiveDays is < 1 or > 3650)
            throw new ArgumentException("Retention policy is invalid.", nameof(policy));
    }
}
