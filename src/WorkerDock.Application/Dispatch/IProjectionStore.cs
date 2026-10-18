namespace WorkerDock.Application.Dispatch;

public interface IProjectionStore
{
    ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPendingAsync(int limit, CancellationToken ct);
    ValueTask AcknowledgeAsync(Guid intentId, string payloadDigest, CancellationToken ct);
    ValueTask RecordFailureAsync(Guid intentId, string safeCode, DateTimeOffset retryAtUtc, CancellationToken ct);
}

// Business state is read only here. Attempt admission/ownership belongs to the worker start gate.
public interface IDispatchStateStore
{
    ValueTask<DispatchState> ReadAsync(ProjectionEnvelope envelope, int attemptOrdinal, CancellationToken ct);
}

public sealed record DispatchState(string State, bool AttemptExists, DispatchIntent Intent);

// Published projections still need bounded integrity reviews after their initial acknowledgement.
public interface IProjectionReviewStore
{
    ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPublishedAsync(int limit, CancellationToken ct);
    ValueTask CompleteReviewAsync(Guid intentId, bool retired, DateTimeOffset reviewAtUtc, CancellationToken ct);
}
