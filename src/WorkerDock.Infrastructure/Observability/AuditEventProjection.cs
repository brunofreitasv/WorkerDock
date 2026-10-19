using WorkerDock.Application.Monitoring;

namespace WorkerDock.Infrastructure.Observability;

public sealed record AuditEventRecord(Guid EventId, string EventType, string ActorSubject, string AuthenticationMode,
    Guid CorrelationId, DateTimeOffset OccurredAtUtc, string? DetailsJson = null);

/// <summary>Projects audit rows without carrying the details payload into monitoring responses or telemetry.</summary>
public static class AuditEventProjection
{
    public static AuditSummaryDto Project(AuditEventRecord row) =>
        new(row.EventId, row.EventType, row.ActorSubject, row.AuthenticationMode, row.CorrelationId, row.OccurredAtUtc);
}
