using System.Globalization;
using global::Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;

namespace WorkerDock.Quartz.Dispatch;

public sealed class QuartzPublisher(IScheduler scheduler, JobType jobType)
{
    public static JobKey JobKey(DispatchIntent intent) => new($"{intent.ExecutionId:N}.{intent.DeliveryId:N}.{intent.AttemptOrdinal}", intent.TemplateId);
    public static TriggerKey TriggerKey(DispatchIntent intent) => new(JobKey(intent).Name, intent.TemplateId);

    public async ValueTask PublishAsync(DispatchIntent intent, CancellationToken ct) => await EnsurePublishedAsync(intent, ct);

    internal async ValueTask<bool> EnsurePublishedAsync(DispatchIntent intent, CancellationToken ct)
    {
        if (intent.ExecutionId == Guid.Empty || intent.DeliveryId == Guid.Empty || intent.AttemptOrdinal < 1 ||
            intent.EntityRevision < 1 || string.IsNullOrWhiteSpace(intent.TemplateId) || intent.TemplateId.Length > 100 ||
            intent.TemplateId.Contains('|') || intent.PayloadDigest != intent.ComputeDigest())
            throw new WorkerDockException("ProjectionIntegrityError");
        var key = JobKey(intent);
        var data = new JobDataMap
        {
            ["ExecutionId"] = intent.ExecutionId.ToString("N"), ["DeliveryId"] = intent.DeliveryId.ToString("N"),
            ["AttemptOrdinal"] = intent.AttemptOrdinal.ToString(CultureInfo.InvariantCulture), ["TemplateId"] = intent.TemplateId,
            ["EntityRevision"] = intent.EntityRevision.ToString(CultureInfo.InvariantCulture),
            ["DueUtc"] = intent.DueUtc.UtcTicks.ToString(CultureInfo.InvariantCulture), ["PayloadDigest"] = intent.PayloadDigest
        };
        var job = JobBuilder.Create().OfType(jobType).WithIdentity(key).UsingJobData(data).StoreDurably().RequestRecovery()
            .DisallowConcurrentExecution(false).PersistJobDataAfterExecution(false).Build();
        var trigger = TriggerBuilder.Create().WithIdentity(TriggerKey(intent)).ForJob(key).WithExecutionGroup(intent.TemplateId).StartAt(intent.DueUtc)
            .WithPriority(intent.Priority).WithRetryPolicy(RetryPolicy.None)
            .WithSimpleSchedule(x => x.WithRepeatCount(0).WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
        try
        {
            // Racing publishers may lose either insert. Re-read the stored identity instead of replacing it.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var stored = await scheduler.GetJobDetail(key, ct);
                if (stored is null)
                {
                    try { await scheduler.ScheduleJob(job, trigger, cancellationToken: ct); return true; }
                    catch (ObjectAlreadyExistsException) { continue; }
                }
                if (stored.JobType != jobType || !stored.Durable || !stored.RequestsRecovery || stored.JobDataMap.Count != data.Count ||
                    data.Any(pair => stored.JobDataMap[pair.Key] is not string value || value != (string)pair.Value!))
                    throw new WorkerDockException("ProjectionIntegrityError");
                var existing = await scheduler.GetTrigger(trigger.Key, ct);
                if (existing is not null)
                {
                    if (existing.JobKey != key || existing.JobDataMap.Count != 0 || existing.RetryPolicy != RetryPolicy.None ||
                        existing.StartTimeUtc < intent.DueUtc || existing.ExecutionGroup != intent.TemplateId ||
                        existing is not ISimpleTrigger { RepeatCount: 0, MisfireInstruction: SimpleTriggerMisfireInstruction.FireNow })
                        throw new WorkerDockException("ProjectionIntegrityError");
                    var state = await scheduler.GetTriggerState(trigger.Key, ct);
                    if (state is TriggerState.Error or TriggerState.Complete)
                        throw new WorkerDockException("ProjectionTriggerError");
                    return false;
                }
                try { await scheduler.ScheduleJob(trigger, cancellationToken: ct); return true; }
                catch (ObjectAlreadyExistsException) { }
            }
            throw new WorkerDockException("ProjectionPublicationRace");
        }
        catch (SchedulerException) { throw new WorkerDockException("QuartzStoreUnavailable"); }
    }
}
