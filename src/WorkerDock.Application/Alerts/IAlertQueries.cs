using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;

namespace WorkerDock.Application.Alerts;

public sealed record AlertIncidentDto(Guid IncidentId, Guid RuleId, string RuleName, string Condition, string SubjectKey,
    string State, DateTimeOffset OpenedAtUtc, DateTimeOffset LastObservedAtUtc, DateTimeOffset? ResolvedAtUtc, string? SafeCode);

public sealed record AlertDeliveryDto(Guid DeliveryId, Guid IncidentId, string RuleName, string SubjectKey, string Kind, string Status, int AttemptCount,
    DateTimeOffset NextAvailableAtUtc, DateTimeOffset? CompletedAtUtc, string? SafeCode);

public sealed record AlertRuleOptionDto(Guid RuleId, string Name);
public sealed record IncidentFilter(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string State, Guid? RuleId);
public sealed record DeliveryFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Status, string? Kind);

public interface IAlertQueries
{
    ValueTask<Page<AlertRuleOptionDto>> RuleOptionsAsync(CancellationToken ct);
    ValueTask<Page<AlertIncidentDto>> IncidentsAsync(IncidentFilter filter, PageRequest page, CancellationToken ct);
    ValueTask<Page<AlertDeliveryDto>> DeliveriesAsync(DeliveryFilter filter, PageRequest page, CancellationToken ct);
}
