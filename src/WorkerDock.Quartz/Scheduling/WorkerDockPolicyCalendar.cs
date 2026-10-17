using global::Quartz;
using WorkerDock.Application.Common;

namespace WorkerDock.Quartz.Scheduling;

/// <summary>WorkerDock v1 filters native Quartz candidates; Quartz owns all cron parsing.</summary>
public sealed class WorkerDockPolicyCalendar : ICalendar
{
    public const int SchemaVersion = 1;
    public const string Policy = "AndDays.SkipGaps.EarlierOffset.V1";
    public const int CandidateLimit = 10_000;
    private readonly TimeZoneInfo zone;
    private readonly CronExpression native;
    private readonly CronExpression nominalDayOfMonth;
    private readonly CronExpression nominalDayOfWeek;

    public WorkerDockPolicyCalendar(string expression, string zoneId)
    {
        var fields = Fields(expression);
        Expression = expression;
        ZoneId = zoneId;
        zone = ResolveZone(zoneId);
        native = Parse(expression, zone);
        // Keeping one day field unrestricted asks Quartz to match each restriction independently.
        nominalDayOfMonth = Parse($"{fields[0]} {fields[1]} {fields[2]} {fields[3]} *", TimeZoneInfo.Utc);
        nominalDayOfWeek = Parse($"{fields[0]} {fields[1]} * {fields[3]} {fields[4]}", TimeZoneInfo.Utc);
    }

    public string Expression { get; }
    public string ZoneId { get; }
    public string? Description
    {
        get => null;
        set { if (value is not null) throw new WorkerDockException("InvalidCalendarMetadata"); }
    }
    public ICalendar? CalendarBase
    {
        get => null;
        set { if (value is not null) throw new WorkerDockException("InvalidCalendarMetadata"); }
    }

    public bool IsTimeIncluded(DateTimeOffset timeStampUtc)
    {
        var local = TimeZoneInfo.ConvertTime(timeStampUtc, zone);
        if (local.Ticks % TimeSpan.TicksPerMinute != 0) return false;
        var wall = DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified);
        var nominal = new DateTimeOffset(wall, TimeSpan.Zero);
        if (!nominalDayOfMonth.IsSatisfiedBy(nominal) || !nominalDayOfWeek.IsSatisfiedBy(nominal)) return false;
        return !zone.IsAmbiguousTime(wall) || local.Offset == zone.GetAmbiguousTimeOffsets(wall).Max();
    }

    public DateTimeOffset GetNextIncludedTimeUtc(DateTimeOffset timeStampUtc)
    {
        if (timeStampUtc >= DateTimeOffset.MaxValue.AddSeconds(-1)) return DateTimeOffset.MaxValue;
        if (IsTimeIncluded(timeStampUtc)) return timeStampUtc.ToUniversalTime();
        var cursor = timeStampUtc;
        for (var evaluated = 0; evaluated < CandidateLimit; evaluated++)
        {
            var next = native.GetNextValidTimeAfter(cursor);
            if (next is null) return DateTimeOffset.MaxValue;
            if (next <= cursor) throw new WorkerDockException("ScheduleSearchLimit");
            if (IsTimeIncluded(next.Value)) return next.Value.ToUniversalTime();
            cursor = next.Value;
        }
        throw new WorkerDockException("ScheduleSearchLimit");
    }

    public ICalendar Clone() => new WorkerDockPolicyCalendar(Expression, ZoneId);

    internal static TimeZoneInfo ResolveZone(string zoneId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zoneId)) throw new WorkerDockException("InvalidTimeZone");
            var resolved = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            if (!resolved.HasIanaId && zoneId != "UTC") throw new WorkerDockException("InvalidTimeZone");
            return resolved;
        }
        catch (TimeZoneNotFoundException) { throw new WorkerDockException("InvalidTimeZone"); }
        catch (InvalidTimeZoneException) { throw new WorkerDockException("InvalidTimeZone"); }
    }

    internal static CronExpression Parse(string expression, TimeZoneInfo zone)
    {
        Fields(expression);
        try { return CronExpression.Parse(expression, CronFormat.Unix).WithTimeZone(zone); }
        catch (FormatException) { throw new WorkerDockException("InvalidCron"); }
    }

    private static string[] Fields(string expression)
    {
        // A dialect allowlist, not a parser: values, lists, ranges and steps are parsed only by Quartz.
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 256 ||
            expression.Any(c => !(char.IsAsciiDigit(c) || char.IsWhiteSpace(c) || c is '*' or ',' or '-' or '/')))
            throw new WorkerDockException("InvalidCron");
        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) throw new WorkerDockException("InvalidCron");
        return fields;
    }
}

