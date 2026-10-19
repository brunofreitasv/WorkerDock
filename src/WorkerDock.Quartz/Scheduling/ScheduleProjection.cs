using System.Globalization;
using global::Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Quartz.Scheduling;

/// <summary>Projects the current business schedule through Quartz public APIs after reading desired state again.</summary>
public sealed class ScheduleProjection(IScheduler scheduler, IScheduleProjectionStore store, IScheduleEvaluator evaluator)
{
    private const string Group = "WorkerDock.Scheduled";

    public ValueTask<int> ReconcileZoneRuleChangesAsync(CancellationToken ct) =>
        store is IScheduleProjectionMaintenance maintenance
            ? maintenance.ReconcileZoneRuleChangesAsync(ct)
            : ValueTask.FromResult(0);

    public async ValueTask ApplyAsync(Guid scheduleId, long revision, CancellationToken ct)
    {
        if (scheduleId == Guid.Empty || revision < 1) throw new WorkerDockException("ProjectionIntegrityError");
        var desired = await store.ReadProjectionAsync(scheduleId, ct);
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(Group), ct);
        var obsolete = keys.Where(key => key.Name.StartsWith(scheduleId.ToString("N") + ".", StringComparison.Ordinal)).ToArray();
        if (desired is null || desired.Removed || desired.Paused || !desired.Enabled || desired.Archived)
        {
            foreach (var obsoleteKey in obsolete) await scheduler.DeleteJob(obsoleteKey, ct);
            return;
        }

        var definition = desired.Definition;
        if (evaluator.Validate(definition).Count != 0) throw new WorkerDockException("ProjectionIntegrityError");
        var keyName = scheduleId.ToString("N") + "." + definition.Revision.ToString(CultureInfo.InvariantCulture);
        var key = new JobKey(keyName, Group);
        var triggerKey = new TriggerKey(keyName, Group);
        var calendarName = "wd-policy-" + scheduleId.ToString("N") + "-" + desired.ZoneRulesFingerprint;
        if (definition.Kind == "Cron")
            await scheduler.AddCalendar(calendarName, new WorkerDockPolicyCalendar(definition.Cron!, definition.ZoneId), AddCalendarOptions.Replacing, ct);

        var data = new JobDataMap { ["ScheduleId"] = scheduleId.ToString("N"), ["ScheduleRevision"] = definition.Revision.ToString(CultureInfo.InvariantCulture) };
        var job = JobBuilder.Create<OccurrenceDispatchJob>().WithIdentity(key).UsingJobData(data).StoreDurably().RequestRecovery().Build();
        var trigger = evaluator is QuartzScheduleEvaluator quartz
            ? quartz.CreateTrigger(definition, triggerKey, calendarName)
            : throw new WorkerDockException("UnsupportedProjection");
        if (await scheduler.GetJobDetail(key, ct) is { } existing)
        {
            if (existing.JobType != typeof(OccurrenceDispatchJob) || existing.JobDataMap.Count != data.Count ||
                data.Any(pair => existing.JobDataMap[pair.Key] is not string value || value != (string)pair.Value!))
                throw new WorkerDockException("ProjectionIntegrityError");
            if (await scheduler.GetTrigger(triggerKey, ct) is { } oldTrigger)
            {
                if (oldTrigger.JobKey != key || oldTrigger.StartTimeUtc != trigger.StartTimeUtc || oldTrigger.CalendarName != calendarName)
                    throw new WorkerDockException("ProjectionIntegrityError");
            }
            else await scheduler.ScheduleJob(trigger, cancellationToken: ct);
        }
        else await scheduler.ScheduleJob(job, trigger, cancellationToken: ct);

        foreach (var old in obsolete.Where(existingKey => existingKey != key)) await scheduler.DeleteJob(old, ct);
    }
}
