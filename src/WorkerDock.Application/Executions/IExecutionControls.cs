using WorkerDock.Application.Common;

namespace WorkerDock.Application.Executions;

public interface IExecutionControls
{
    ValueTask<CommandReceipt> StopAsync(Guid executionId, bool force, string reason, ActorContext actor, CancellationToken ct);
    ValueTask<CommandReceipt> ResolveAsync(Guid executionId, string decision, string reason, ActorContext actor, CancellationToken ct);
}
