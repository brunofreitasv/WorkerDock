using System.Globalization;
using global::Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Alerts;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Quartz.Alerts;

[DisallowConcurrentExecution]
[RetryPolicy(0)]
public sealed class AlertDeliveryJob(
    IAlertDeliveryStore deliveries,
    IReadOnlyCollection<IAlertSink> sinks,
    ISecretVault secrets,
    Guid ownerBootId) : IJob
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(context.MergedJobDataMap.GetString("deliveryId"), "N", out var deliveryId) ||
            !int.TryParse(context.MergedJobDataMap.GetString("attemptOrdinal"), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) ||
            deliveryId == Guid.Empty || ordinal < 1 || ownerBootId == Guid.Empty)
            return;

        var claim = await deliveries.TryBeginAsync(deliveryId, ordinal, ownerBootId, cancellationToken);
        if (claim is null) return;

        var sink = sinks.SingleOrDefault(candidate => string.Equals(candidate.Type, claim.Sink.Type, StringComparison.Ordinal));
        var result = !claim.Sink.Enabled
            ? AlertDeliveryResult.PermanentFailure("AlertSinkDisabled")
            : sink is null
                ? AlertDeliveryResult.PermanentFailure("AlertSinkUnavailable")
            : await DeliverWithLeaseAsync(sink, claim, cancellationToken);

        await deliveries.CompleteAsync(claim.Lease, Sanitize(result), cancellationToken);
    }

    private async ValueTask<AlertDeliveryResult> DeliverWithLeaseAsync(IAlertSink sink, AlertDeliveryClaim claim, CancellationToken ct)
    {
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = sink.DeliverAsync(claim.Sink.Configuration, claim.Envelope, secrets, claim.Sink.Timeout, sendCancellation.Token).AsTask();
        while (!send.IsCompleted)
        {
            var renewal = Task.Delay(RenewalInterval, ct);
            var completed = await Task.WhenAny(send, renewal);
            if (completed == send) break;

            try
            {
                if (await deliveries.RenewAsync(claim.Lease, ct)) continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { sendCancellation.Cancel(); throw; }
            catch { }

            sendCancellation.Cancel();
            return AlertDeliveryResult.TransientFailure("AlertLeaseLost");
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

    public static async ValueTask PublishAsync(IScheduler scheduler, Guid deliveryId, int attemptOrdinal, CancellationToken ct)
    {
        if (deliveryId == Guid.Empty || attemptOrdinal < 1) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        var suffix = deliveryId.ToString("N") + "." + attemptOrdinal.ToString(CultureInfo.InvariantCulture);
        var jobKey = new JobKey(suffix, "WorkerDock.AlertDelivery");
        var triggerKey = new TriggerKey(suffix, "WorkerDock.AlertDelivery");
        var data = new JobDataMap
        {
            ["deliveryId"] = deliveryId.ToString("N"),
            ["attemptOrdinal"] = attemptOrdinal.ToString(CultureInfo.InvariantCulture)
        };
        var job = JobBuilder.Create<AlertDeliveryJob>().WithIdentity(jobKey).UsingJobData(data).StoreDurably().RequestRecovery().Build();
        try { await scheduler.AddJob(job, cancellationToken: ct); }
        catch (ObjectAlreadyExistsException)
        {
            var existing = await scheduler.GetJobDetail(jobKey, ct);
            if (existing?.JobType != typeof(AlertDeliveryJob) || existing.JobDataMap.GetString("deliveryId") != deliveryId.ToString("N") ||
                existing.JobDataMap.GetString("attemptOrdinal") != attemptOrdinal.ToString(CultureInfo.InvariantCulture))
                throw;
        }

        if (await scheduler.GetTrigger(triggerKey, ct) is not null) return;
        var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(jobKey).WithExecutionGroup("alerts")
            .StartNow().WithRetryPolicy(RetryPolicy.None)
            .WithSimpleSchedule(schedule => schedule.WithRepeatCount(0)).Build();
        try { await scheduler.ScheduleJob(trigger, cancellationToken: ct); }
        catch (ObjectAlreadyExistsException) { }
    }
}
