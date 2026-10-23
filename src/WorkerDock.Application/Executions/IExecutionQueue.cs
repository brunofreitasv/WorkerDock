namespace WorkerDock.Application.Executions;

public interface IExecutionQueue
{
    ValueTask<IReadOnlyList<Guid>> ReadReadyAsync(int limit, CancellationToken ct);
}
