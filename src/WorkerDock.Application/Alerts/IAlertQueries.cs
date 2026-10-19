namespace WorkerDock.Application.Alerts;

public sealed record AlertIncidentDto(Guid IncidentId, Guid RuleId, string RuleName, string Condition, string SubjectKey,
    string State, DateTimeOffset OpenedAtUtc, DateTimeOffset LastObservedAtUtc, DateTimeOffset? ResolvedAtUtc, string? SafeCode);

public sealed record AlertDeliveryDto(Guid DeliveryId, Guid IncidentId, string Kind, string Status, int AttemptCount,
    DateTimeOffset NextAvailableAtUtc, DateTimeOffset? CompletedAtUtc, string? SafeCode);

public interface IAlertQueries
{
    ValueTask<IReadOnlyList<AlertIncidentDto>> ListIncidentsAsync(int limit, CancellationToken ct);
    ValueTask<IReadOnlyList<AlertDeliveryDto>> ListDeliveriesAsync(int limit, CancellationToken ct);
}
