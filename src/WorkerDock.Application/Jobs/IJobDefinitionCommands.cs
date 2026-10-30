using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Jobs;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Application.Jobs;

public interface IJobDefinitionCommands
{
    ValueTask<CommandReceipt> SaveDefinitionAsync(Guid jobId, JobRevision proposed, ScheduleEditorInput? schedule,
        MissedWorkPolicy? missedPolicy, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
}
