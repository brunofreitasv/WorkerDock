using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Infrastructure.Scheduling;

/// <summary>Evaluates WorkerDock's restricted five-field cron dialect without a scheduler dependency.</summary>
public sealed class FiveFieldScheduleEvaluator : IScheduleEvaluator
{
    private const int CandidateLimit = 10_000;
    private const int CycleYears = 400;

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
        if (schedule.Kind == "Once") return schedule.OneTimeUtc > cursor ? [schedule.OneTimeUtc!.Value] : [];
        if (cursor >= DateTimeOffset.MaxValue.AddSeconds(-1)) return [];

        var cron = CronFields.Parse(schedule.Cron!);
        var zone = ResolveZone(schedule.ZoneId);
        var localCursor = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(cursor, zone).DateTime, DateTimeKind.Unspecified);
        var firstDay = localCursor.Date;
        var lastDay = firstDay.Year <= DateTime.MaxValue.Year - CycleYears
            ? firstDay.AddYears(CycleYears)
            : DateTime.MaxValue.Date;
        var results = new List<DateTimeOffset>(count);
        var candidates = 0;
        for (var day = firstDay; day <= lastDay;)
        {
            if (cron.Months[day.Month] && cron.DaysOfMonth[day.Day] && cron.DaysOfWeek[(int)day.DayOfWeek])
            {
                foreach (var hour in Values(cron.Hours))
                foreach (var minute in Values(cron.Minutes))
                {
                    if (++candidates > CandidateLimit) throw new WorkerDockException("ScheduleSearchLimit");
                    var local = day.AddHours(hour).AddMinutes(minute);
                    if (zone.IsInvalidTime(local)) continue;
                    var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
                    // The larger offset maps to the earlier UTC instant during an overlap.
                    var occurrence = new DateTimeOffset(local, offsets.Max()).ToUniversalTime();
                    if (occurrence <= cursor) continue;
                    results.Add(occurrence);
                    if (results.Count == count) return results;
                }
            }
            if (day == lastDay) break;
            day = day.AddDays(1);
        }
        if (results.Count == 0 && cron.HasRestrictedDayOfWeek)
            throw new WorkerDockException("ScheduleSearchLimit");
        return results;
    }

    private static IEnumerable<int> Values(bool[] field)
    {
        for (var value = 0; value < field.Length; value++)
            if (field[value]) yield return value;
    }

    public DateTimeOffset ResolveLocalOnce(DateTime localUnspecified, string zoneId, TimeSpan? selectedOffset)
    {
        if (localUnspecified.Kind != DateTimeKind.Unspecified) throw new WorkerDockException("InvalidLocalTimeKind");
        var zone = ResolveZone(zoneId);
        if (zone.IsInvalidTime(localUnspecified)) throw new WorkerDockException("NonexistentLocalTime");
        var ambiguous = zone.IsAmbiguousTime(localUnspecified);
        if (ambiguous && selectedOffset is null) throw new WorkerDockException("AmbiguousLocalTime");
        var offsets = ambiguous ? zone.GetAmbiguousTimeOffsets(localUnspecified) : [zone.GetUtcOffset(localUnspecified)];
        var offset = selectedOffset ?? offsets[0];
        if (!offsets.Contains(offset)) throw new WorkerDockException("InvalidLocalOffset");
        try { return new DateTimeOffset(localUnspecified, offset).ToUniversalTime(); }
        catch (ArgumentException) { throw new WorkerDockException("InvalidLocalTime"); }
    }

    private static TimeZoneInfo ResolveZone(string zoneId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zoneId)) throw new WorkerDockException("InvalidTimeZone");
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            if (!zone.HasIanaId && zoneId != "UTC") throw new WorkerDockException("InvalidTimeZone");
            return zone;
        }
        catch (TimeZoneNotFoundException) { throw new WorkerDockException("InvalidTimeZone"); }
        catch (InvalidTimeZoneException) { throw new WorkerDockException("InvalidTimeZone"); }
    }

    private static void Check(ScheduleDefinition schedule)
    {
        if (schedule.ScheduleId == Guid.Empty || schedule.Revision < 1 || schedule.Kind is not ("Once" or "Cron") ||
            schedule.ActivatedUtc.Offset != TimeSpan.Zero || schedule.ActivatedUtc >= DateTimeOffset.MaxValue.AddSeconds(-1))
            throw new WorkerDockException("InvalidSchedule");
        _ = ResolveZone(schedule.ZoneId);
        if (schedule.Kind == "Once")
        {
            if (schedule.Cron is not null || schedule.OneTimeUtc is null || schedule.OneTimeUtc.Value.Offset != TimeSpan.Zero)
                throw new WorkerDockException("InvalidSchedule");
        }
        else
        {
            if (schedule.OneTimeUtc is not null) throw new WorkerDockException("InvalidSchedule");
            _ = CronFields.Parse(schedule.Cron!);
        }
    }

    private sealed class CronFields
    {
        private CronFields(bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek)
        { Minutes = minutes; Hours = hours; DaysOfMonth = daysOfMonth; Months = months; DaysOfWeek = daysOfWeek; }
        public bool[] Minutes { get; }
        public bool[] Hours { get; }
        public bool[] DaysOfMonth { get; }
        public bool[] Months { get; }
        public bool[] DaysOfWeek { get; }
        public bool HasRestrictedDayOfWeek => Enumerable.Range(0, 7).Any(day => !DaysOfWeek[day]);

        public static CronFields Parse(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 256 ||
                expression.Any(c => !(char.IsAsciiDigit(c) || char.IsWhiteSpace(c) || c is '*' or ',' or '-' or '/')))
                throw new WorkerDockException("InvalidCron");
            var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5) throw new WorkerDockException("InvalidCron");
            return new CronFields(ParseField(fields[0], 0, 59), ParseField(fields[1], 0, 23),
                ParseField(fields[2], 1, 31), ParseField(fields[3], 1, 12), ParseField(fields[4], 0, 7, sundaySeven: true));
        }

        private static bool[] ParseField(string field, int minimum, int maximum, bool sundaySeven = false)
        {
            var values = new bool[maximum + 1];
            foreach (var item in field.Split(','))
            {
                if (item.Length == 0) throw new WorkerDockException("InvalidCron");
                var stepParts = item.Split('/');
                if (stepParts.Length > 2 || (stepParts.Length == 2 && !int.TryParse(stepParts[1], out var parsedStep)) ||
                    (stepParts.Length == 2 && int.Parse(stepParts[1]) < 1)) throw new WorkerDockException("InvalidCron");
                var step = stepParts.Length == 2 ? int.Parse(stepParts[1]) : 1;
                var range = stepParts[0];
                int start, end;
                if (range == "*") { start = minimum; end = maximum; }
                else if (range.Contains('-'))
                {
                    var bounds = range.Split('-');
                    if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                        throw new WorkerDockException("InvalidCron");
                }
                else
                {
                    if (!int.TryParse(range, out start)) throw new WorkerDockException("InvalidCron");
                    end = stepParts.Length == 2 ? maximum : start;
                }
                if (start < minimum || end > maximum || start > end) throw new WorkerDockException("InvalidCron");
                for (var value = start; ;)
                {
                    values[sundaySeven && value == 7 ? 0 : value] = true;
                    if (end - value < step) break;
                    value += step;
                }
            }
            if (!values.Any(value => value)) throw new WorkerDockException("InvalidCron");
            return values;
        }
    }
}
