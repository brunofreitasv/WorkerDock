using WorkerDock.Application.Common;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Application.Scheduling;

public sealed record ScheduleEditorInput(string Kind, string? Cron, string ZoneId, DateTime? LocalOnce, TimeSpan? SelectedOffset);
public sealed record SchedulePreviewItem(DateTimeOffset Utc, TimeSpan Offset);
public sealed record ScheduleEditorDto(Guid JobId, ScheduleDefinition Definition, MissedWorkPolicy Policy, byte[] Version, bool Paused);
public sealed record ScheduleProjectionState(ScheduleDefinition Definition, bool Enabled, bool Archived, bool Paused, bool Removed, string ZoneRulesFingerprint);

public interface IScheduleProjectionStore
{
    ValueTask<ScheduleProjectionState?> ReadProjectionAsync(Guid scheduleId, CancellationToken ct);
}

public interface IScheduleCommands
{
    ValueTask<CommandReceipt> SaveAsync(Guid jobId, ScheduleDefinition definition, MissedWorkPolicy policy,
        byte[]? expectedVersion, ActorContext actor, CancellationToken ct);
    ValueTask<ScheduleEditorDto> GetAsync(Guid jobId, CancellationToken ct);
    ValueTask<CommandReceipt> DeleteAsync(Guid jobId, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
    ValueTask<IReadOnlyList<DateTimeOffset>> PreviewAsync(ScheduleDefinition definition, DateTimeOffset afterUtc, int count, CancellationToken ct);
}
