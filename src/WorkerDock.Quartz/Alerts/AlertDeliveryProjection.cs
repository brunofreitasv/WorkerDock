using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;

namespace WorkerDock.Quartz.Alerts;

public sealed class AlertDeliveryProjection(global::Quartz.IScheduler scheduler)
{
    public async ValueTask ApplyAsync(ProjectionEnvelope envelope, CancellationToken ct)
    {
        var (deliveryId, ordinal) = Decode(envelope);
        try { await AlertDeliveryJob.PublishAsync(scheduler, deliveryId, ordinal, ct); }
        catch (global::Quartz.SchedulerException) { throw new WorkerDockException("QuartzStoreUnavailable"); }
    }

    private static (Guid DeliveryId, int Ordinal) Decode(ProjectionEnvelope envelope)
    {
        try
        {
            var payload = envelope.Payload;
            if (envelope.Kind != "AlertDispatch" || envelope.SchemaVersion != 1 || envelope.IntentId == Guid.Empty ||
                payload.ValueKind != JsonValueKind.Object ||
                payload.EnumerateObject().Any(property => property.Name is not ("deliveryId" or "attemptOrdinal")) ||
                payload.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() != 1))
                throw new WorkerDockException("ProjectionIntegrityError");

            var deliveryId = payload.GetProperty("deliveryId").GetGuid();
            var ordinal = payload.GetProperty("attemptOrdinal").GetInt32();
            if (deliveryId == Guid.Empty || ordinal < 1 || deliveryId != envelope.EntityId || ordinal != envelope.EntityRevision ||
                envelope.PayloadDigest != ProjectionEnvelope.AlertDigest(deliveryId, ordinal))
                throw new WorkerDockException("ProjectionIntegrityError");
            return (deliveryId, ordinal);
        }
        catch (WorkerDockException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new WorkerDockException("ProjectionIntegrityError"); }
    }
}
