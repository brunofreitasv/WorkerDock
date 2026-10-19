using WorkerDock.Application.Common;
using WorkerDock.Domain.Alerts;

namespace WorkerDock.Application.Alerts;

public sealed record AlertRuleEditorDto(AlertRule Rule, byte[] Version);

public interface IAlertRules
{
    ValueTask<IReadOnlyList<AlertRuleEditorDto>> ListAsync(CancellationToken ct);
    ValueTask<AlertRuleEditorDto> GetAsync(Guid id, CancellationToken ct);
    ValueTask<CommandReceipt> SaveAsync(AlertRule rule, byte[]? expectedVersion, ActorContext actor, CancellationToken ct);
}
