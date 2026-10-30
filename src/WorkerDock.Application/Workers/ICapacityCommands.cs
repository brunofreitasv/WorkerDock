using WorkerDock.Application.Common;
namespace WorkerDock.Application.Workers;

public interface ICapacityCommands
{
    ValueTask<CommandReceipt> SetJobControlsAsync(Guid jobId, int limit, bool pauseOnNeedsReview, ActorContext actor, CancellationToken ct);
    ValueTask<CommandReceipt> SetJobLimitAsync(Guid jobId, int limit, ActorContext actor, CancellationToken ct);
}
