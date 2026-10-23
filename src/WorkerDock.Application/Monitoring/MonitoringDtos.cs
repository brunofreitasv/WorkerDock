namespace WorkerDock.Application.Monitoring;

public sealed record PageRequest(string? Cursor, int Limit);
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record MonitoringFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Search, string? State, string? TemplateId);

public sealed record DashboardDto(
    Guid InstanceId,
    DateTimeOffset ObservedUtc,
    string Overall,
    int Queued,
    int Running,
    int NeedsReview,
    int Succeeded24h,
    int Failed24h,
    int LongRunning,
    int Upcoming,
    bool RuntimeReady,
    int HostCeiling,
    DateTimeOffset? RuntimeHeartbeatUtc,
    IReadOnlyList<UpcomingJobDto> UpcomingItems,
    IReadOnlyList<RecentExecutionDto> RecentItems);

public sealed record UpcomingJobDto(Guid JobId, string JobName, string TemplateId, DateTimeOffset ScheduledForUtc, string State);
public sealed record RecentExecutionDto(Guid ExecutionId, Guid JobId, string JobName, string State, DateTimeOffset CreatedAtUtc, Guid CorrelationId);
public sealed record JobSummaryDto(Guid JobId, long Revision, string Name, string TemplateId, string TemplateVersion, bool Enabled, bool Archived, bool HasSchedule, bool SchedulePaused, int ActiveExecutions, int Failed24h, DateTimeOffset CreatedAtUtc);
public sealed record ExecutionAttemptDto(Guid AttemptId, int AttemptNumber, Guid? WorkerId, string State, DateTimeOffset? StartedAtUtc, DateTimeOffset? EndedAtUtc, DateTimeOffset? DeadlineUtc, string? SafeCode, bool LocalExitConfirmed, bool EffectsMayHaveOccurred);
public sealed record ExecutionEventDto(Guid EventId, Guid? AttemptId, string State, DateTimeOffset OccurredAtUtc);
public sealed record ExecutionDetailsDto(Guid ExecutionId, Guid JobId, string JobName, string TemplateId, string State, string Certainty, string TriggerKind, DateTimeOffset CreatedAtUtc, DateTimeOffset AvailableAtUtc, DateTimeOffset? ScheduledForUtc, Guid CorrelationId, bool StopRequested, bool ForceStop, IReadOnlyList<ExecutionAttemptDto> Attempts, IReadOnlyList<ExecutionEventDto> Timeline);
public sealed record AuditSummaryDto(Guid EventId, string EventType, string ActorSubject, string AuthenticationMode, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
public sealed record RuntimeStatusDto(Guid? BootId, bool Ready, int HostCeiling, DateTimeOffset? LastHeartbeatUtc);
public sealed record TenantHealthDto(Guid InstanceId, string Overall, IReadOnlyDictionary<string, string> Components, DateTimeOffset ObservedUtc);
