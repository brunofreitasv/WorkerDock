using WorkerDock.Contracts.Alerts;
using WorkerDock.Application.Common;

namespace WorkerDock.Application.Alerts;

public sealed record AlertDeliveryLease(Guid DeliveryId, int AttemptOrdinal, long Generation, Guid OwnerBootId, DateTimeOffset ExpiresUtc);

public sealed record AlertDeliveryClaim(AlertDeliveryLease Lease, AlertEnvelope Envelope, AlertSinkDefinition Sink);

public sealed record DueAlertDelivery(Guid DeliveryId, int AttemptOrdinal);

public interface IAlertDeliveryStore
{
    ValueTask<IReadOnlyList<DueAlertDelivery>> ReadDueAsync(int limit, CancellationToken ct);
    ValueTask<AlertDeliveryClaim?> TryBeginAsync(Guid deliveryId, int expectedAttemptOrdinal, Guid ownerBootId, CancellationToken ct);
    ValueTask<bool> RenewAsync(AlertDeliveryLease lease, CancellationToken ct);
    ValueTask<bool> CompleteAsync(AlertDeliveryLease lease, AlertDeliveryResult result, CancellationToken ct);
    ValueTask<CommandReceipt> RetryFailedAsync(Guid deliveryId, int additionalAttempts, ActorContext actor, CancellationToken ct);
}
