namespace WorkerDock.Contracts.Logging;
public interface ISafeExecutionLog
{
    ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct);
}
