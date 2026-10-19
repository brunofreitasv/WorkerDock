using WorkerDock.Application.Common;

namespace WorkerDock.Application.Jobs;

public interface IJobDuplicator
{
    ValueTask<CommandReceipt> DuplicateAsync(Guid sourceJobId, Guid targetJobId, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
}
