using System.Collections.Concurrent;
using Quartz;
using WorkerDock.Quartz.Scheduling;
namespace WorkerDock.IntegrationTests;
public sealed class CalendarExecutionTests
{
    private static DateTimeOffset U(string value) => DateTimeOffset.Parse(value);

    [Theory]
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T06:30:00Z", "2026-03-10T06:30:00Z")]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z")]
    [InlineData("* * * * *", "America/New_York", "2026-11-01T05:58:59Z", "2026-11-01T05:59:00Z", "2026-11-01T07:00:00Z")]
    [InlineData("*/15 * * * *", "Australia/Lord_Howe", "2026-04-04T14:44:59Z", "2026-04-04T14:45:00Z", "2026-04-04T15:30:00Z")]
    [InlineData("0,30 2 * * *", "Australia/Lord_Howe", "2026-10-03T14:00:00Z", "2026-10-03T15:30:00Z", "2026-10-04T15:00:00Z")]
    [InlineData("0 0 13 * 5", "Etc/UTC", "2026-10-01T00:00:00Z", "2026-11-13T00:00:00Z", "2027-08-13T00:00:00Z")]
    public async Task Memory_job_executes_filtered_nominal_time(string expression, string zone, string start, string first, string next)
    {
        var clock = new AdvancingFixtureClock(U(start));
        var key = Guid.NewGuid().ToString("N");
        var completion = CalendarExecutionProbe.Expect(key);
        await using var factory = QuartzSchedulerBuilder.Create(builder =>
        {
            builder.ConfigureScheduler(x => { x.InstanceName = key; x.InstanceId = key; });
            builder.UseTimeProvider(clock);
            builder.UseDefaultThreadPool(1);
            builder.UseInMemoryStore();
        }).Build();
        var scheduler = await factory.GetScheduler();
        try
        {
            await scheduler.AddCalendar("policy", new WorkerDockPolicyCalendar(expression, zone));
            await scheduler.ScheduleJob(JobBuilder.Create<CalendarExecutionProbe>().WithIdentity(key).Build(),
                TriggerBuilder.Create(clock).WithIdentity(key).WithCalendarName("policy").StartAt(U(start))
                    .WithCronSchedule(CronExpression.Parse(expression, CronFormat.Unix).WithTimeZone(TimeZoneInfo.FindSystemTimeZoneById(zone)),
                        x => x.WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)).Build());
            Assert.Equal(U(first), (await scheduler.GetTrigger(new(key)))!.NextFireTimeUtc);
            clock.AdvanceTo(U(first));
            await scheduler.Start();
            Assert.Equal(U(first), await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(U(next), (await scheduler.GetTrigger(new(key)))!.NextFireTimeUtc);
        }
        finally
        {
            await scheduler.Shutdown();
            CalendarExecutionProbe.Forget(key);
        }
    }
}
internal sealed class AdvancingFixtureClock(DateTimeOffset initial) : TimeProvider
{
    private long ticks = initial.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void AdvanceTo(DateTimeOffset next) => Interlocked.Exchange(ref ticks, next.UtcTicks);
}
public sealed class CalendarExecutionProbe : IJob
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<DateTimeOffset>> Pending = new();
    public static Task<DateTimeOffset> Expect(string key)
    {
        var completion = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Pending.TryAdd(key, completion));
        return completion.Task;
    }
    public static void Forget(string key) => Pending.TryRemove(key, out _);
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (Pending.TryGetValue(context.JobDetail.Key.Name, out var completion))
            completion.TrySetResult(context.ScheduledFireTimeUtc!.Value);
        return ValueTask.CompletedTask;
    }
}
