using System.Text;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;
using Quartz.Serialization.SystemTextJson;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Quartz.Hosting;
using WorkerDock.Quartz.Scheduling;
using WorkerDock.DatabaseMigrator;
namespace WorkerDock.IntegrationTests;
public sealed class CalendarPersistenceTests
{
    private static DateTimeOffset U(string value) => DateTimeOffset.Parse(value);
    private static SystemTextJsonObjectSerializer Serializer()
    {
        var registry = new SystemTextJsonSerializerRegistry();
        registry.AddCalendarSerializer(new WorkerDockPolicyCalendarSerializer());
        return new(registry);
    }
    private static IOperableTrigger Trigger(string expression, string zone, string start) =>
        (IOperableTrigger)TriggerBuilder.Create(new FixtureClock(U(start).AddSeconds(-1))).WithIdentity("policy").WithCalendarName("policy-v1")
            .StartAt(U(start)).WithCronSchedule(CronExpression.Parse(expression, CronFormat.Unix).WithTimeZone(TimeZoneInfo.FindSystemTimeZoneById(zone)),
                x => x.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)).Build();
    private static DateTimeOffset[] Occurrences(IOperableTrigger trigger, ICalendar calendar, int count)
    {
        if (trigger.NextFireTimeUtc is null) trigger.ComputeFirstFireTimeUtc(calendar);
        var results = new List<DateTimeOffset>();
        while (results.Count < count && trigger.NextFireTimeUtc is { } next)
        {
            results.Add(next);
            trigger.Triggered(calendar);
        }
        return results.ToArray();
    }

    [Theory]
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T06:30:00Z", "2026-03-10T06:30:00Z")]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z")]
    [InlineData("* * * * *", "America/New_York", "2026-11-01T05:59:00Z", "2026-11-01T05:59:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("*/15 * * * *", "America/New_York", "2026-11-01T05:45:00Z", "2026-11-01T05:45:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("0,30 2 * * *", "Australia/Lord_Howe", "2026-10-03T14:00:00Z", "2026-10-03T15:30:00Z", "2026-10-04T15:00:00Z")]
    [InlineData("45 1 * * *", "Australia/Lord_Howe", "2026-04-04T14:00:00Z", "2026-04-04T14:45:00Z", "2026-04-05T15:15:00Z")]
    [InlineData("0 0 13 * 5", "Etc/UTC", "2026-10-01T00:00:00Z", "2026-11-13T00:00:00Z", "2027-08-13T00:00:00Z")]
    public async Task Memory_trigger_and_serializer_preserve_policy(string expression, string zone, string start, string first, string second)
    {
        var calendar = new WorkerDockPolicyCalendar(expression, zone);
        var serializer = Serializer();
        var bytes = serializer.Serialize<ICalendar>(calendar);
        var copy = serializer.Deserialize<ICalendar>(bytes)!;
        var expected = new[] { U(first), U(second) };
        Assert.Equal(expected, Occurrences(Trigger(expression, zone, start), copy, 2));
        var schedule = new ScheduleDefinition(Guid.NewGuid(), 1, "Cron", expression, zone, null, U(start).AddTicks(-1));
        Assert.Equal(expected, new QuartzScheduleEvaluator().Next(schedule, U(start).AddTicks(-1), 2));
        Assert.Equal(expected, Occurrences(Trigger(expression, zone, start), calendar.Clone(), 2));
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var scheduler = await topology.CreateScheduleAsync(new(id, "policy", 1), default);
        await scheduler.AddCalendar("policy-v1", copy);
        await scheduler.ScheduleJob(JobBuilder.Create<CalendarProbe>().WithIdentity("probe").Build(), Trigger(expression, zone, start));
        var stored = (IOperableTrigger)(await scheduler.GetTrigger(new("policy")))!;
        Assert.Equal(U(first), stored.NextFireTimeUtc);
        Assert.Equal(expected, Occurrences(stored, (await scheduler.GetCalendar("policy-v1"))!, 2));
    }

    [Fact]
    public void Unknown_payload_version_fails_closed()
    {
        var serializer = Serializer();
        var bytes = serializer.Serialize<ICalendar>(new WorkerDockPolicyCalendar("* * * * *", "Etc/UTC"));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"SchemaVersion\":1", text);
        Assert.ThrowsAny<Exception>(() => serializer.Deserialize<ICalendar>(Encoding.UTF8.GetBytes(text.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99"))));
    }

    [Theory]
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T06:30:00Z", "2026-03-10T06:30:00Z")]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z")]
    [InlineData("* * * * *", "America/New_York", "2026-11-01T05:59:00Z", "2026-11-01T05:59:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("*/15 * * * *", "America/New_York", "2026-11-01T05:45:00Z", "2026-11-01T05:45:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("0,30 2 * * *", "Australia/Lord_Howe", "2026-10-03T14:00:00Z", "2026-10-03T15:30:00Z", "2026-10-04T15:00:00Z")]
    [InlineData("45 1 * * *", "Australia/Lord_Howe", "2026-04-04T14:00:00Z", "2026-04-04T14:45:00Z", "2026-04-05T15:15:00Z")]
    [InlineData("0 0 13 * 5", "Etc/UTC", "2026-10-01T00:00:00Z", "2026-11-13T00:00:00Z", "2027-08-13T00:00:00Z")]
    [Trait("Store", "Sql")]
    public async Task Restart_preserves_filtered_occurrences(string expression, string zone, string start, string first, string second)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot).ApplyAsync("tenant", "0002_quartz", default);

        DateTimeOffset[] before;
        await using (var topology = new QuartzTopology(new(database.ConnectionString, database.InstanceId)))
        {
            var scheduler = await topology.CreateScheduleAsync(new(database.InstanceId, "before", 1), default);
            var calendar = new WorkerDockPolicyCalendar(expression, zone);
            await scheduler.AddCalendar("policy-v1", calendar);
            await scheduler.ScheduleJob(JobBuilder.Create<CalendarProbe>().WithIdentity("probe").Build(), Trigger(expression, zone, start));
            before = Occurrences((IOperableTrigger)(await scheduler.GetTrigger(new("policy")))!, (await scheduler.GetCalendar("policy-v1"))!, 2);
            await scheduler.Shutdown();
        }
        await using var restarted = new QuartzTopology(new(database.ConnectionString, database.InstanceId));
        var restored = await restarted.CreateScheduleAsync(new(database.InstanceId, "after", 1), default);
        var after = Occurrences((IOperableTrigger)(await restored.GetTrigger(new("policy")))!, (await restored.GetCalendar("policy-v1"))!, 2);
        Assert.Equal(new[] { U(first), U(second) }, before);
        Assert.Equal(before, after);
        await restored.Shutdown();
    }
}
public sealed class CalendarProbe : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}


internal sealed class FixtureClock(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

