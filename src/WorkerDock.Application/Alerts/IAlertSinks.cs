using System.Text.Json;
using WorkerDock.Application.Common;

namespace WorkerDock.Application.Alerts;

public sealed record AlertSinkDefinition(
    Guid Id,
    string Name,
    string Type,
    JsonElement Configuration,
    int MaxAttempts,
    TimeSpan RetryBaseDelay,
    TimeSpan RetryMaxDelay,
    TimeSpan Timeout,
    bool Enabled);

public sealed record AlertSinkEditorDto(AlertSinkDefinition Definition, byte[] Version);

public interface IAlertSinks
{
    ValueTask<IReadOnlyList<AlertSinkEditorDto>> ListAsync(CancellationToken ct);
    ValueTask<AlertSinkEditorDto> GetAsync(Guid id, CancellationToken ct);
    ValueTask<CommandReceipt> SaveAsync(AlertSinkDefinition sink, byte[]? expectedVersion, ActorContext actor, CancellationToken ct);
}
