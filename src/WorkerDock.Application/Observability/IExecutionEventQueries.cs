namespace WorkerDock.Application.Observability;

public interface IExecutionEventQueries
{
    ValueTask<ExecutionEventPage> ReadAsync(Guid executionId, string? cursor, int limit, CancellationToken ct);
}

public sealed record ExecutionEventPage(IReadOnlyList<ExecutionEventDto> Items, string? NextCursor);

public sealed record ExecutionEventDto(
    Guid EventId,
    Guid ExecutionId,
    Guid? AttemptId,
    string Source,
    long Sequence,
    DateTimeOffset AtUtc,
    string Code,
    IReadOnlyDictionary<string, string> SafeFields);
