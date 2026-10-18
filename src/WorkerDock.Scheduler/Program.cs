using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SchedulerService>();
await builder.Build().RunAsync();

internal sealed class SchedulerService(IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Guid.TryParse(configuration["Tenant:InstanceId"], out var instanceId) || instanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(configuration["Tenant:ConnectionString"]))
            throw new WorkerDockException("SchedulerConfigurationUnavailable");
        var connection = configuration["Tenant:ConnectionString"]!;
        await using var topology = new QuartzTopology(new(connection, instanceId));
        var node = new QuartzNodeOptions(instanceId, Guid.NewGuid().ToString("N"), 4);
        var handle = await topology.CreateExecutionPublisherAsync(node, stoppingToken);
        var store = new SqlProjectionStore(new(connection, instanceId));
        var publisher = new QuartzPublisher(handle, typeof(ExecuteTemplateJob));
        var reconciler = new DispatchReconciler(store, store, publisher, TimeProvider.System);
        var control = await topology.CreateScheduleAsync(node, new MaintenanceFactory(reconciler, new SqlAttemptStore(new(connection, instanceId))), stoppingToken);
        try
        {
            await ProjectionJob.RegisterAsync(control, stoppingToken);
            await RecoveryReviewJob.RegisterAsync(control, stoppingToken);
            await control.Start(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await control.Shutdown(true, shutdown.Token);
            await handle.Shutdown(false, shutdown.Token);
        }
    }

    private sealed class MaintenanceFactory(DispatchReconciler reconciler, IAttemptStore attempts) : IJobFactory
    {
        public ValueTask<JobScope> CreateJob(TriggerFiredBundle bundle, IScheduler scheduler, CancellationToken cancellationToken = default)
        {
            IJob job = bundle.JobDetail.JobType == typeof(ProjectionJob) ? new ProjectionJob(reconciler)
                : bundle.JobDetail.JobType == typeof(RecoveryReviewJob) ? new RecoveryReviewJob(attempts)
                : throw new SchedulerException("UnsupportedControlJob");
            return ValueTask.FromResult(new JobScope(job));
        }
        public ValueTask ReturnJob(JobScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
