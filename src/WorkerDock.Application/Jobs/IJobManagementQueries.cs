using WorkerDock.Application.Monitoring;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Application.Jobs;

public sealed record JobListFilter(string View, string? Search);
public sealed record JobListItemDto(Guid JobId, string Name, string TemplateId, string TemplateVersion,
    bool Enabled, bool Archived, string? ScheduleKind, string? ScheduleZoneId, bool SchedulePaused,
    DateTimeOffset ConfigurationUpdatedUtc);
public sealed record JobManagementDetailDto(JobEditorDto Editor, ScheduleEditorInput? Schedule,
    MissedWorkPolicy? MissedPolicy, bool SchedulePaused, ScheduleEditorInput? PreviousRemovedSchedule);
public sealed record JobExecutionItemDto(Guid ExecutionId, long JobRevision, string TriggerKind, string State,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, TimeSpan? Duration);

public interface IJobManagementQueries
{
    ValueTask<Page<JobListItemDto>> ListAsync(JobListFilter filter, PageRequest page, CancellationToken ct);
    ValueTask<JobManagementDetailDto> GetAsync(Guid jobId, CancellationToken ct);
    ValueTask<Page<JobExecutionItemDto>> ExecutionsAsync(Guid jobId, PageRequest page, CancellationToken ct);
}
