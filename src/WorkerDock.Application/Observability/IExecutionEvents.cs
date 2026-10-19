namespace WorkerDock.Application.Observability;

public sealed record ExecutionEvent(
    Guid ExecutionId,
    Guid? AttemptId,
    string Source,
    long Sequence,
    DateTimeOffset AtUtc,
    string Code,
    IReadOnlyDictionary<string, string> SafeFields);

public interface IExecutionEvents
{
    ValueTask AppendAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken ct);
}
