using WorkerDock.Application.Common;
using WorkerDock.Domain.Jobs;

namespace WorkerDock.Application.Jobs;

public sealed record JobEditorDto(JobRevision Revision, byte[] Version, bool Enabled, bool Archived, int ConcurrencyLimit);

public interface IJobCommands
{
    ValueTask<CommandReceipt> SaveAsync(JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct);
    ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ActorContext actor, CancellationToken ct);
    ValueTask<CommandReceipt> GetCommandAsync(Guid commandId, CancellationToken ct);
    ValueTask<JobEditorDto> GetEditableAsync(Guid jobId, CancellationToken ct);
}
