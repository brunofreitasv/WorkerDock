using global::Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Retention;

namespace WorkerDock.Quartz.Retention;

[DisallowConcurrentExecution]
[RetryPolicy(0)]
public sealed class RetentionJob(IRetentionService retention, TimeProvider clock) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        await retention.RunBatchAsync(clock.GetUtcNow(), 100, bounded.Token);
    }

    public static async ValueTask RegisterAsync(IScheduler scheduler, CancellationToken ct)
    {
        const string name = "retention-maintenance";
        var key = new JobKey(name, "WorkerDock.Control");
        await scheduler.AddJob(JobBuilder.Create<RetentionJob>().WithIdentity(key).StoreDurably().RequestRecovery().Build(),
            AddJobOptions.Replacing, ct);
        var triggerKey = new TriggerKey(name, "WorkerDock.Control");
        if (await scheduler.GetTrigger(triggerKey, ct) is not null) return;
        var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(key).WithExecutionGroup("maintenance")
            .StartNow().WithRetryPolicy(RetryPolicy.None)
            .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever()
                .WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
        try { await scheduler.ScheduleJob(trigger, cancellationToken: ct); }
        catch (ObjectAlreadyExistsException) { }
    }
}
