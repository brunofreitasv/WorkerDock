using WorkerDock.Application.Common;
namespace WorkerDock.Application.Workers;

public interface ICapacityCommands
{
    ValueTask<CommandReceipt> SetTemplateLimitAsync(string templateId, int limit, ActorContext actor, CancellationToken ct);
    ValueTask<CommandReceipt> SetJobLimitAsync(Guid jobId, int limit, ActorContext actor, CancellationToken ct);
}
