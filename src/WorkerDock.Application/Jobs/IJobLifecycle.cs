using WorkerDock.Application.Common;

namespace WorkerDock.Application.Jobs;

public interface IJobLifecycle
{
    ValueTask<CommandReceipt> ApplyAsync(Guid jobId, string action, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
}
