namespace WorkerDock.Application.Monitoring;

public interface IMonitoringQueries
{
    ValueTask<DashboardDto> DashboardAsync(CancellationToken ct);
    ValueTask<Page<JobSummaryDto>> JobsAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct);
    ValueTask<Page<ExecutionHistoryItemDto>> ExecutionsAsync(ExecutionHistoryFilter filter, PageRequest page, CancellationToken ct);
    ValueTask<ExecutionDetailsDto> ExecutionAsync(Guid executionId, CancellationToken ct);
    ValueTask<Page<AuditSummaryDto>> AuditAsync(AuditHistoryFilter filter, PageRequest page, CancellationToken ct);
    ValueTask<IReadOnlyList<string>> AuditEventTypesAsync(CancellationToken ct);
    ValueTask<RuntimeStatusDto> WorkersAsync(CancellationToken ct);
    ValueTask<TenantHealthDto> HealthAsync(CancellationToken ct);
}
