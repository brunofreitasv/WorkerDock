using WorkerDock.Application.Alerts;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class AlertDeliveryRunner(
    IAlertDeliveryStore deliveries,
    IReadOnlyCollection<IAlertSink> sinks,
    ISecretVault secrets,
    Guid ownerBootId,
    TimeSpan? renewalInterval = null)
{
    private readonly TimeSpan renewalInterval = renewalInterval ?? TimeSpan.FromSeconds(10);

    public async ValueTask RunAsync(DueAlertDelivery due, CancellationToken ct)
    {
        if (due.DeliveryId == Guid.Empty || due.AttemptOrdinal < 1 || ownerBootId == Guid.Empty) return;
        if (renewalInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(renewalInterval));

        var claim = await deliveries.TryBeginAsync(due.DeliveryId, due.AttemptOrdinal, ownerBootId, ct);
        if (claim is null) return;

        var sink = sinks.SingleOrDefault(candidate => string.Equals(candidate.Type, claim.Sink.Type, StringComparison.Ordinal));
        AlertDeliveryResult? result;
        if (!claim.Sink.Enabled)
            result = AlertDeliveryResult.PermanentFailure("AlertSinkDisabled");
        else if (sink is null)
            result = AlertDeliveryResult.PermanentFailure("AlertSinkUnavailable");
        else
            result = await DeliverWithLeaseAsync(sink, claim, ct);

        // A send may have reached the remote sink after this process lost its lease.
        // The next owner will recover the expired attempt; this owner cannot record a result.
        if (result is not null)
            await deliveries.CompleteAsync(claim.Lease, Sanitize(result), ct);
    }

    private async ValueTask<AlertDeliveryResult?> DeliverWithLeaseAsync(IAlertSink sink, AlertDeliveryClaim claim, CancellationToken ct)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<AlertDeliveryResult> send;
        try
        {
            send = sink.DeliverAsync(claim.Sink.Configuration, claim.Envelope, secrets, claim.Sink.Timeout, sendCancellation.Token).AsTask();
        }
        catch
        {
            return AlertDeliveryResult.TransientFailure("AlertSinkFailed");
        }

        while (!send.IsCompleted)
        {
            var renewal = Task.Delay(renewalInterval, ct);
            var completed = await Task.WhenAny(send, renewal);
            if (completed == send) break;

            try
            {
                if (await deliveries.RenewAsync(claim.Lease, ct)) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                sendCancellation.Cancel();
                throw;
            }
            catch { }

            sendCancellation.Cancel();
            _ = send.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return null;
        }

        try { return await send; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return AlertDeliveryResult.TransientFailure("AlertDeliveryCancelled"); }
        catch { return AlertDeliveryResult.TransientFailure("AlertSinkFailed"); }
    }

    private static AlertDeliveryResult Sanitize(AlertDeliveryResult result)
    {
        if (result.Outcome is not ("Accepted" or "TransientFailure" or "PermanentFailure") ||
            string.IsNullOrWhiteSpace(result.SafeCode) || result.SafeCode.Length > 80 ||
            result.SafeCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return AlertDeliveryResult.TransientFailure("AlertSinkFailed");
        return result with { RetryAfter = result.RetryAfter is { } delay && delay > TimeSpan.Zero ? delay : null };
    }
}
