using global::Quartz;

namespace WorkerDock.Quartz.Dispatch;

[DisallowConcurrentExecution]
[RetryPolicy(0)]
public sealed class ProjectionJob(DispatchReconciler reconciler) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        await reconciler.ReconcileAsync(100, bounded.Token);
    }

    public static async ValueTask RegisterAsync(IScheduler scheduler, CancellationToken ct)
    {
        foreach (var (name, seconds) in new[] { ("projection", 15), ("reconciliation", 60) })
        {
            var key = new JobKey(name, "WorkerDock.Control");
            await scheduler.AddJob(JobBuilder.Create<ProjectionJob>().WithIdentity(key).StoreDurably().RequestRecovery().Build(),
                AddJobOptions.Replacing, ct);
            var triggerKey = new TriggerKey(name, "WorkerDock.Control");
            if (await scheduler.GetTrigger(triggerKey, ct) is not null) continue;
            var trigger = TriggerBuilder.Create().WithIdentity(triggerKey).ForJob(key).WithExecutionGroup("maintenance")
                .StartNow().WithRetryPolicy(RetryPolicy.None)
                .WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromSeconds(seconds)).RepeatForever()
                    .WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
            try { await scheduler.ScheduleJob(trigger, cancellationToken: ct); }
            catch (ObjectAlreadyExistsException) { }
        }
    }
}
