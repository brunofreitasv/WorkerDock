using System.Reflection;
using System.Runtime.ExceptionServices;
using global::Quartz;
using WorkerDock.Application.Common;

namespace WorkerDock.Quartz.Hosting;

// IScheduler is the required public port. A deny-by-default facade keeps its much wider
// administration/start API out of the publisher; new Quartz methods stay denied.
public class PublisherScheduler : DispatchProxy
{
    private IScheduler scheduler = null!;
    private bool ownsScheduler;
    private bool shutdown;
    internal static IScheduler Create(IScheduler scheduler, bool ownsScheduler)
    {
        var proxy = Create<IScheduler, PublisherScheduler>();
        var state = (PublisherScheduler)proxy;
        state.scheduler = scheduler;
        state.ownsScheduler = ownsScheduler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException();
        if (method.Name == "get_Status") return shutdown ? SchedulerStatus.Shutdown : SchedulerStatus.Created;
        if (method.Name == "GetStatus") return ValueTask.FromResult(shutdown ? SchedulerStatus.Shutdown : SchedulerStatus.Created);
        if (method.Name == "Shutdown")
        {
            shutdown = true;
            return ownsScheduler ? Forward(method, args) : ValueTask.CompletedTask;
        }
        if (shutdown) throw new WorkerDockException("PublisherShutdown");
        if (method.Name is "get_SchedulerName" or "get_SchedulerInstanceId" or "GetSchedulerInstanceId") return Forward(method, args);
        if (method.Name is not ("ScheduleJob" or "AddJob")) throw new WorkerDockException("PublisherOperationForbidden");
        foreach (var argument in args ?? [])
        {
            if (argument is IJobDetail job) Validate(job.JobDataMap);
            if (argument is ITrigger trigger) Validate(trigger.JobDataMap);
            // Batch overloads are intentionally not exposed; one opaque delivery per call.
            if (argument is System.Collections.IEnumerable && argument is not string)
                throw new WorkerDockException("PublisherOperationForbidden");
        }
        return Forward(method, args);
    }
    private object? Forward(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(scheduler, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    private static void Validate(JobDataMap data)
    {
        foreach (var pair in data)
            if (pair.Key is not ("ExecutionId" or "DeliveryId" or "AttemptOrdinal") || pair.Value is not string)
                throw new WorkerDockException("InvalidQuartzJobData");
    }
}
