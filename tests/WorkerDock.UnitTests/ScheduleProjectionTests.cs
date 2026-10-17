using Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Quartz.Scheduling;
namespace WorkerDock.UnitTests;
public sealed class ScheduleProjectionTests
{
    private static DateTimeOffset U(string value) => DateTimeOffset.Parse(value);
    [Theory]
    [InlineData("2028-01-01T00:00:00Z", "2028-01-01T00:01:00Z")]
    [InlineData("2028-01-01T00:00:59.9999999Z", "2028-01-01T00:01:00Z")]
    public void Projected_trigger_preserves_activation_boundary(string activation, string expected)
    {
        var schedule = new ScheduleDefinition(Guid.NewGuid(), 1, "Cron", "* * * * *", "Etc/UTC", null, U(activation));
        var evaluator = new QuartzScheduleEvaluator();
        var trigger = evaluator.CreateTrigger(schedule, new("revision"), "calendar");
        Assert.Equal(U(expected), trigger.GetFireTimeAfter(U(activation)));
        Assert.Equal(U(expected), Assert.Single(evaluator.Next(schedule, U(activation), 1)));
        Assert.Equal("calendar", trigger.CalendarName);
        Assert.Equal(CronTriggerMisfireInstruction.FireAndProceed, ((ICronTrigger)trigger).MisfireInstruction);
    }

    [Fact]
    public void Once_projection_is_native_and_expires()
    {
        var schedule = new ScheduleDefinition(Guid.NewGuid(), 1, "Once", null, "Etc/UTC", U("2028-01-01T01:00:00Z"), U("2028-01-01T00:00:00Z"));
        var evaluator = new QuartzScheduleEvaluator();
        var trigger = Assert.IsAssignableFrom<ISimpleTrigger>(evaluator.CreateTrigger(schedule, new("once"), "unused"));
        Assert.Equal(SimpleTriggerMisfireInstruction.FireNow, trigger.MisfireInstruction);
        Assert.Equal(schedule.OneTimeUtc, trigger.GetFireTimeAfter(schedule.ActivatedUtc));
        Assert.Null(trigger.GetFireTimeAfter(schedule.OneTimeUtc));
        Assert.Null(trigger.CalendarName);
    }

    [Fact]
    public void Calendar_advances_strictly_through_gap_and_later_overlap()
    {
        Assert.Equal(DateTimeOffset.MaxValue, new WorkerDockPolicyCalendar("* * * * *", "Etc/UTC").GetNextIncludedTimeUtc(DateTimeOffset.MaxValue));
        var gap = new WorkerDockPolicyCalendar("30 2 * * *", "America/New_York");
        Assert.False(gap.IsTimeIncluded(U("2026-03-08T07:00:00Z")));
        Assert.Equal(U("2026-03-09T06:30:00Z"), gap.GetNextIncludedTimeUtc(U("2026-03-08T07:00:00Z")));
        var overlap = new WorkerDockPolicyCalendar("* * * * *", "America/New_York");
        Assert.False(overlap.IsTimeIncluded(U("2026-11-01T06:59:00Z")));
        Assert.Equal(U("2026-11-01T07:00:00Z"), overlap.GetNextIncludedTimeUtc(U("2026-11-01T06:59:00Z")));
        Assert.Equal(U("2026-11-01T05:59:00Z"), overlap.GetNextIncludedTimeUtc(U("2026-11-01T05:59:00Z")));
        var impossible = new WorkerDockPolicyCalendar("* * 31 2 1", "Etc/UTC");
        Assert.Equal("ScheduleSearchLimit", Assert.Throws<WorkerDockException>(() => impossible.GetNextIncludedTimeUtc(U("2026-01-01T00:00:00Z"))).Code);
        Assert.Equal(DateTimeOffset.MaxValue, new WorkerDockPolicyCalendar("0 0 31 2 *", "Etc/UTC").GetNextIncludedTimeUtc(U("2026-01-01T00:00:00Z")));
    }
}

