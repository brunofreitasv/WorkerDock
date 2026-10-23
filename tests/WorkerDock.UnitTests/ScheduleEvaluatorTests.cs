using WorkerDock.Application.Common;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Scheduling;
namespace WorkerDock.UnitTests;
public sealed class ScheduleEvaluatorTests
{
    private readonly FiveFieldScheduleEvaluator evaluator = new();
    private static DateTimeOffset U(string value) => DateTimeOffset.Parse(value);
    private static ScheduleDefinition Cron(string text, string zone = "Etc/UTC") => new(Guid.NewGuid(), 1, "Cron", text, zone, null, U("2026-01-01T00:00:00Z"));

    [Theory]
    [InlineData("0 0 1 1 *", "Etc/UTC", "2026-01-01T00:00:00Z", "2027-01-01T00:00:00Z")]
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T06:30:00Z")]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-01T05:30:00Z")]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z")]
    [InlineData("* * * * *", "America/New_York", "2026-11-01T05:59:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("*/15 * * * *", "America/New_York", "2026-11-01T05:45:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("45 1 * * *", "Australia/Lord_Howe", "2026-04-04T14:00:00Z", "2026-04-04T14:45:00Z")]
    [InlineData("45 1 * * *", "Australia/Lord_Howe", "2026-04-04T14:45:00Z", "2026-04-05T15:15:00Z")]
    [InlineData("15 2 * * *", "Australia/Lord_Howe", "2026-10-03T14:00:00Z", "2026-10-04T15:15:00Z")]
    [InlineData("0,30 2 * * *", "Australia/Lord_Howe", "2026-10-03T14:00:00Z", "2026-10-03T15:30:00Z")]
    [InlineData("0 0 29 2 *", "Etc/UTC", "2026-01-01T00:00:00Z", "2028-02-29T00:00:00Z")]
    [InlineData("0 0 * * 0", "Etc/UTC", "2026-10-01T00:00:00Z", "2026-10-04T00:00:00Z")]
    [InlineData("0 0 * * 7", "Etc/UTC", "2026-10-01T00:00:00Z", "2026-10-04T00:00:00Z")]
    [InlineData("0 0 13 * 5", "Etc/UTC", "2026-10-01T00:00:00Z", "2026-11-13T00:00:00Z")]
    [InlineData("0 9-10 * * 1,3,5", "Etc/UTC", "2026-10-02T09:00:00Z", "2026-10-02T10:00:00Z")]
    public void Cron_policy_fixtures(string expression, string zone, string after, string expected) =>
        Assert.Equal(U(expected), Assert.Single(evaluator.Next(Cron(expression, zone), U(after), 1)));

    [Fact]
    public void Cron_next_results_are_strictly_after_cursor_and_utc_ordered()
    {
        var results = evaluator.Next(Cron("30 1 * * *", "America/New_York"), U("2026-10-31T00:00:00Z"), 3);

        Assert.Equal(new[] { "2026-10-31T05:30:00Z", "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z" },
            results.Select(x => x.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        Assert.All(results, result => Assert.Equal(TimeSpan.Zero, result.Offset));
    }

    [Fact]
    public void Cron_near_datetime_limit_returns_the_next_available_occurrence()
    {
        var schedule = Cron("0 0 * * *") with { ActivatedUtc = U("9999-01-01T00:00:00Z") };

        Assert.Equal(U("9999-01-02T00:00:00Z"), Assert.Single(evaluator.Next(schedule, schedule.ActivatedUtc, 1)));
    }

    [Theory]
    [InlineData("0 0 0 * * *")]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 * JAN MON")]
    [InlineData("@daily")]
    [InlineData("0 24 * * *")]
    [InlineData("0 0 * * 8")]
    public void Rejects_unsupported_or_invalid_cron(string expression) => Assert.Contains("InvalidCron", evaluator.Validate(Cron(expression)));

    [Fact]
    public void Impossible_schedule_is_exhausted() => Assert.Empty(evaluator.Next(Cron("0 0 31 2 *"), U("2026-01-01T00:00:00Z"), 1));

    [Fact]
    public void Bounds_count_and_candidate_search()
    {
        Assert.Equal("InvalidOccurrenceCount", Assert.Throws<WorkerDockException>(() => evaluator.Next(Cron("* * * * *"), U("2026-01-01Z"), 101)).Code);
        Assert.Equal("ScheduleSearchLimit", Assert.Throws<WorkerDockException>(() => evaluator.Next(Cron("* * 31 2 1"), U("2026-01-01Z"), 1)).Code);
    }

    [Fact]
    public void Once_requires_explicit_valid_overlap_offset()
    {
        var local = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal("AmbiguousLocalTime", Assert.Throws<WorkerDockException>(() => evaluator.ResolveLocalOnce(local, "America/New_York", null)).Code);
        Assert.Equal("InvalidLocalOffset", Assert.Throws<WorkerDockException>(() => evaluator.ResolveLocalOnce(local, "America/New_York", TimeSpan.Zero)).Code);
        Assert.Equal(U("2026-11-01T05:30:00Z"), evaluator.ResolveLocalOnce(local, "America/New_York", TimeSpan.FromHours(-4)));
        Assert.Equal(U("2026-11-01T06:30:00Z"), evaluator.ResolveLocalOnce(local, "America/New_York", TimeSpan.FromHours(-5)));
        Assert.Equal("InvalidLocalTimeKind", Assert.Throws<WorkerDockException>(() => evaluator.ResolveLocalOnce(DateTime.SpecifyKind(local, DateTimeKind.Utc), "America/New_York", null)).Code);
        Assert.Equal("NonexistentLocalTime", Assert.Throws<WorkerDockException>(() => evaluator.ResolveLocalOnce(new(2026, 3, 8, 2, 30, 0), "America/New_York", null)).Code);
        Assert.Equal("InvalidLocalOffset", Assert.Throws<WorkerDockException>(() => evaluator.ResolveLocalOnce(new(2026, 1, 1, 12, 0, 0), "America/New_York", TimeSpan.Zero)).Code);
    }

    [Fact]
    public void Once_and_activation_are_exclusive_and_validated()
    {
        var once = new ScheduleDefinition(Guid.NewGuid(), 1, "Once", null, "Etc/UTC", U("2026-01-02T00:00:00Z"), U("2026-01-01T00:00:00Z"));
        Assert.Empty(evaluator.Validate(once));
        Assert.Equal(once.OneTimeUtc, Assert.Single(evaluator.Next(once, once.ActivatedUtc, 100)));
        Assert.Empty(evaluator.Next(once, once.OneTimeUtc!.Value, 1));
        Assert.Contains("InvalidTimeZone", evaluator.Validate(once with { ZoneId = "made-up" }));
        Assert.Contains("InvalidSchedule", evaluator.Validate(once with { Kind = "Other" }));
        Assert.Equal(U("2026-01-01T00:01:00Z"), Assert.Single(evaluator.Next(Cron("* * * * *"), U("2025-01-01T00:00:00Z"), 1)));
    }
}
