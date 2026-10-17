using global::Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Quartz.Scheduling;

public sealed class QuartzScheduleEvaluator : IScheduleEvaluator
{
    public IReadOnlyList<string> Validate(ScheduleDefinition schedule)
    {
        try
        {
            Check(schedule);
            return Next(schedule, schedule.ActivatedUtc, 1).Count == 0 ? ["ImpossibleSchedule"] : [];
        }
        catch (WorkerDockException error) { return [error.Code]; }
    }

    public IReadOnlyList<DateTimeOffset> Next(ScheduleDefinition schedule, DateTimeOffset afterExclusiveUtc, int count)
    {
        if (count is < 1 or > 100) throw new WorkerDockException("InvalidOccurrenceCount");
        Check(schedule);
        var cursor = afterExclusiveUtc > schedule.ActivatedUtc ? afterExclusiveUtc : schedule.ActivatedUtc;
        if (schedule.Kind == "Once")
            return schedule.OneTimeUtc > cursor ? [schedule.OneTimeUtc.Value.ToUniversalTime()] : [];
        if (cursor >= DateTimeOffset.MaxValue.AddSeconds(-1)) return [];
        var calendar = new WorkerDockPolicyCalendar(schedule.Cron!, schedule.ZoneId);
        var trigger = CronTrigger(schedule, cursor, "preview", "preview");
        List<DateTimeOffset> results = [];
        for (var evaluated = 0; evaluated < WorkerDockPolicyCalendar.CandidateLimit; evaluated++)
        {
            var next = trigger.GetFireTimeAfter(cursor);
            if (next is null) return results;
            if (next <= cursor) throw new WorkerDockException("ScheduleSearchLimit");
            cursor = next.Value;
            if (!calendar.IsTimeIncluded(cursor)) continue;
            results.Add(cursor.ToUniversalTime());
            if (results.Count == count) return results;
        }
        throw new WorkerDockException("ScheduleSearchLimit");
    }

    /// <summary>Build a native trigger for a revision; register its policy calendar before scheduling.</summary>
    public ITrigger CreateTrigger(ScheduleDefinition schedule, TriggerKey key, string calendarName)
    {
        var errors = Validate(schedule);
        if (errors.Count != 0) throw new WorkerDockException(errors[0]);
        if (schedule.Kind == "Once")
            return TriggerBuilder.Create().WithIdentity(key).StartAt(schedule.OneTimeUtc!.Value)
                .WithSimpleSchedule(x => x.WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
        return CronTrigger(schedule, schedule.ActivatedUtc.AddSeconds(1), key.Name, calendarName, key.Group);
    }

    private static ITrigger CronTrigger(ScheduleDefinition schedule, DateTimeOffset start, string name, string calendarName, string group = "DEFAULT") =>
        TriggerBuilder.Create().WithIdentity(name, group).StartAt(start).WithCalendarName(calendarName)
            .WithCronSchedule(WorkerDockPolicyCalendar.Parse(schedule.Cron!, WorkerDockPolicyCalendar.ResolveZone(schedule.ZoneId)),
                x => x.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)).Build();

    public DateTimeOffset ResolveLocalOnce(DateTime localUnspecified, string zoneId, TimeSpan? selectedOffset)
    {
        if (localUnspecified.Kind != DateTimeKind.Unspecified) throw new WorkerDockException("InvalidLocalTimeKind");
        var zone = WorkerDockPolicyCalendar.ResolveZone(zoneId);
        if (zone.IsInvalidTime(localUnspecified)) throw new WorkerDockException("NonexistentLocalTime");
        var ambiguous = zone.IsAmbiguousTime(localUnspecified);
        if (ambiguous && selectedOffset is null) throw new WorkerDockException("AmbiguousLocalTime");
        var validOffsets = ambiguous ? zone.GetAmbiguousTimeOffsets(localUnspecified) : [zone.GetUtcOffset(localUnspecified)];
        var offset = selectedOffset ?? validOffsets[0];
        if (!validOffsets.Contains(offset)) throw new WorkerDockException("InvalidLocalOffset");
        try { return new DateTimeOffset(localUnspecified, offset).ToUniversalTime(); }
        catch (ArgumentException) { throw new WorkerDockException("InvalidLocalTime"); }
    }

    private static void Check(ScheduleDefinition schedule)
    {
        if (schedule.ScheduleId == Guid.Empty || schedule.Revision < 1 || schedule.Kind is not ("Once" or "Cron") ||
            schedule.ActivatedUtc.Offset != TimeSpan.Zero || schedule.ActivatedUtc >= DateTimeOffset.MaxValue.AddSeconds(-1))
            throw new WorkerDockException("InvalidSchedule");
        var zone = WorkerDockPolicyCalendar.ResolveZone(schedule.ZoneId);
        if (schedule.Kind == "Once")
        {
            if (schedule.Cron is not null || schedule.OneTimeUtc is null || schedule.OneTimeUtc.Value.Offset != TimeSpan.Zero)
                throw new WorkerDockException("InvalidSchedule");
        }
        else
        {
            if (schedule.OneTimeUtc is not null) throw new WorkerDockException("InvalidSchedule");
            WorkerDockPolicyCalendar.Parse(schedule.Cron!, zone);
        }
    }
}
