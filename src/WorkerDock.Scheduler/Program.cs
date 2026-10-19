using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Application.Scheduling;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Scheduling;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;
using WorkerDock.Quartz.Scheduling;
using WorkerDock.Application.Alerts;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Alerts;
using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Infrastructure.Settings;
using WorkerDock.Quartz.Alerts;
using WorkerDock.Application.Retention;
using WorkerDock.Infrastructure.Retention;
using WorkerDock.Quartz.Retention;

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
        var options = new TenantDatabaseOptions(connection, instanceId);
        var alertDeliveries = new SqlAlertDeliveryStore(options);
        var alertVault = SecretVaultRegistration.CreateDevelopment();
        var alertSinks = new IAlertSink[] { new WebhookAlertSink(new SqlIntegrationSettings(options)) };
        var alertOwnerBootId = Guid.NewGuid();
        var alertEvaluator = new IncidentEvaluator(options);
        var retention = new SqlRetentionService(options, configuration["Retention:ArchiveRoot"]);
        var evaluator = new QuartzScheduleEvaluator();
        var scheduleStore = new SqlScheduleCommands(options, evaluator, TimeProvider.System);
        var occurrenceWriter = new OccurrenceWriter(options, evaluator);
        DispatchReconciler? reconciler = null;
        var attempts = new SqlAttemptStore(options);
        var control = await topology.CreateScheduleAsync(node, new MaintenanceFactory(() => reconciler, attempts, occurrenceWriter, TimeProvider.System,
            alertDeliveries, alertSinks, alertVault, alertOwnerBootId, alertEvaluator, retention), stoppingToken);
        var publisher = new QuartzPublisher(handle, typeof(ExecuteTemplateJob));
        var scheduleProjection = new ScheduleProjection(control, scheduleStore, evaluator);
        var alertProjection = new AlertDeliveryProjection(control);
        reconciler = new DispatchReconciler(store, store, publisher, TimeProvider.System, scheduleProjection, alertProjection);
        try
        {
            await ProjectionJob.RegisterAsync(control, stoppingToken);
            await RecoveryReviewJob.RegisterAsync(control, stoppingToken);
            await AlertEvaluationJob.RegisterAsync(control, stoppingToken);
            await RetentionJob.RegisterAsync(control, stoppingToken);
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

    private sealed class MaintenanceFactory(Func<DispatchReconciler?> reconciler, IAttemptStore attempts, IOccurrenceWriter occurrences, TimeProvider clock,
        IAlertDeliveryStore alertDeliveries, IReadOnlyCollection<IAlertSink> alertSinks, ISecretVault secrets, Guid alertOwnerBootId,
        IAlertEvaluator alertEvaluator, IRetentionService retention) : IJobFactory
    {
        public ValueTask<JobScope> CreateJob(TriggerFiredBundle bundle, IScheduler scheduler, CancellationToken cancellationToken = default)
        {
            IJob job = bundle.JobDetail.JobType == typeof(ProjectionJob) ? new ProjectionJob(reconciler() ?? throw new SchedulerException("ProjectionNotReady"))
                : bundle.JobDetail.JobType == typeof(RecoveryReviewJob) ? new RecoveryReviewJob(attempts)
                : bundle.JobDetail.JobType == typeof(OccurrenceDispatchJob) ? new OccurrenceDispatchJob(occurrences,clock)
                : bundle.JobDetail.JobType == typeof(AlertDeliveryJob) ? new AlertDeliveryJob(alertDeliveries, alertSinks, secrets, alertOwnerBootId)
                : bundle.JobDetail.JobType == typeof(AlertEvaluationJob) ? new AlertEvaluationJob(alertEvaluator, clock)
                : bundle.JobDetail.JobType == typeof(RetentionJob) ? new RetentionJob(retention, clock)
                : throw new SchedulerException("UnsupportedControlJob");
            return ValueTask.FromResult(new JobScope(job));
        }
        public ValueTask ReturnJob(JobScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
