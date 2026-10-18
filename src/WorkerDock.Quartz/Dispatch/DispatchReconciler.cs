using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;

namespace WorkerDock.Quartz.Dispatch;

public sealed class DispatchReconciler(IProjectionStore projections, IDispatchStateStore states, QuartzPublisher publisher, TimeProvider clock)
{
    private int reviewTurn;
    public async ValueTask<int> ReconcileAsync(int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var reviews = projections as IProjectionReviewStore;
        // Reserve work for both streams so a full pending backlog cannot hide errored published triggers.
        var reviewLimit = reviews is null ? 0 : batchSize == 1 ? Interlocked.Increment(ref reviewTurn) % 2 : batchSize / 2;
        var pending = batchSize > reviewLimit ? await projections.ReadPendingAsync(batchSize - reviewLimit, ct) : [];
        var published = reviewLimit > 0 ? await reviews!.ReadPublishedAsync(reviewLimit, ct) : [];
        var count = 0;
        foreach (var envelope in pending.Concat(published).DistinctBy(x => x.IntentId).Take(batchSize))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ordinal = Decode(envelope);
                var business = await states.ReadAsync(envelope, ordinal, ct);
                var intent = business.Intent;
                if (intent.ExecutionId != envelope.EntityId || intent.EntityRevision != envelope.EntityRevision ||
                    intent.DeliveryId != envelope.IntentId || intent.AttemptOrdinal != ordinal)
                    throw new WorkerDockException("ProjectionIntegrityError");
                // A consumed trigger is not evidence that a new attempt was authorized.
                var retired = business.AttemptExists || business.State is "Running" or "NeedsReview" or
                    "Succeeded" or "Failed" or "Stopped" or "TimedOut" or "Skipped";
                var repaired = false;
                if (!retired)
                {
                    if (business.State is not ("Queued" or "RetryWaiting")) throw new WorkerDockException("ProjectionHeld");
                    repaired = await publisher.EnsurePublishedAsync(intent, ct);
                    await publisher.AgeAsync(intent, clock.GetUtcNow(), ct);
                }
                await projections.AcknowledgeAsync(envelope.IntentId, envelope.PayloadDigest, ct);
                if (reviews is not null) await reviews.CompleteReviewAsync(envelope.IntentId, retired, clock.GetUtcNow().AddMinutes(1), ct);
                if (repaired || retired || pending.Any(x => x.IntentId == envelope.IntentId)) count++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (WorkerDockException error)
            {
                await projections.RecordFailureAsync(envelope.IntentId, SafeCode(error.Code), clock.GetUtcNow().AddMinutes(1), ct);
            }
        }
        if (projections is IProjectionMaintenanceStore maintenance)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            try { await maintenance.MaintainAsync(budget.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (WorkerDockException) { } // Publication already committed; retry bounded maintenance on the next tick.
        }
        return count;
    }

    private static int Decode(ProjectionEnvelope envelope)
    {
        if (envelope.Kind != "ExecutionDispatch" || envelope.SchemaVersion != 1) throw new WorkerDockException("UnsupportedProjection");
        try
        {
            var payload = envelope.Payload;
            if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(p => p.Name is not ("executionId" or "jobRevision" or "attemptOrdinal" or "capacityRevision")) ||
                payload.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() != 1))
                throw new WorkerDockException("ProjectionIntegrityError");
            var id = payload.GetProperty("executionId").GetGuid();
            var revision = payload.GetProperty("jobRevision").GetInt64();
            var ordinal = payload.TryGetProperty("attemptOrdinal", out var number) ? number.GetInt32() : 1;
            if (envelope.IntentId == Guid.Empty || id == Guid.Empty || revision < 1 || ordinal < 1 || id != envelope.EntityId || revision != envelope.EntityRevision ||
                envelope.PayloadDigest != ProjectionEnvelope.ExecutionDigest(id, revision, ordinal, payload.TryGetProperty("capacityRevision", out var epoch) ? epoch.GetInt64() : 0))
                throw new WorkerDockException("ProjectionIntegrityError");
            return ordinal;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new WorkerDockException("ProjectionIntegrityError"); }
    }

    private static string SafeCode(string code) => code is "UnsupportedProjection" or "ProjectionIntegrityError" or
        "ProjectionTriggerError" or "ProjectionPublicationRace" or "ProjectionHeld" or "QuartzStoreUnavailable"
        ? code : "ProjectionStoreUnavailable";
}
