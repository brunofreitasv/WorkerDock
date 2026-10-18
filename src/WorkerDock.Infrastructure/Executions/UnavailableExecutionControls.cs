using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;

namespace WorkerDock.Infrastructure.Executions;

public sealed class UnavailableExecutionControls : IExecutionControls
{
    public ValueTask<CommandReceipt> StopAsync(Guid executionId, bool force, string reason, ActorContext actor, CancellationToken ct) =>
        ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> ResolveAsync(Guid executionId, string decision, string reason, ActorContext actor, CancellationToken ct) =>
        ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
}
