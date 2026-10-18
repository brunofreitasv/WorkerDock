using global::Quartz;
using WorkerDock.Application.Executions;

namespace WorkerDock.Quartz.Dispatch;

[DisallowConcurrentExecution]
[RetryPolicy(0)]
public sealed class RecoveryReviewJob(IAttemptStore store) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        await store.ReviewExpiredAsync(100, bounded.Token);
    }
    public static async ValueTask RegisterAsync(IScheduler scheduler, CancellationToken ct)
    {
        var key = new JobKey("attempt-recovery", "WorkerDock.Control");
        await scheduler.AddJob(JobBuilder.Create<RecoveryReviewJob>().WithIdentity(key).StoreDurably().RequestRecovery().Build(), AddJobOptions.Replacing, ct);
        var triggerKey = new TriggerKey(key.Name, key.Group);
        if (await scheduler.GetTrigger(triggerKey, ct) is not null) return;
        try
        {
            await scheduler.ScheduleJob(TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(key).WithExecutionGroup("maintenance")
                .StartNow().WithRetryPolicy(RetryPolicy.None).WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromSeconds(15)).RepeatForever()
                    .WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build(), cancellationToken: ct);
        }
        catch (ObjectAlreadyExistsException) { }
    }
}
