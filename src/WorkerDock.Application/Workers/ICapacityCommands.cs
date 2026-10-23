using WorkerDock.Application.Common;
namespace WorkerDock.Application.Workers;

public sealed record CapacitySnapshot(long DesiredRevision, long EffectiveRevision, string State, IReadOnlyDictionary<string, int> TemplateLimits);
public interface ICapacityCommands
{
    ValueTask<CommandReceipt> SetTemplateLimitAsync(string templateId, int limit, ActorContext actor, CancellationToken ct);
    ValueTask<CommandReceipt> SetJobLimitAsync(Guid jobId, int limit, ActorContext actor, CancellationToken ct);
}
