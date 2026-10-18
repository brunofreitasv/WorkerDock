using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;
using WorkerDock.Runtime.Ownership;
using WorkerDock.Runtime.Processes;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));
builder.Services.AddHostedService<WorkerService>();
await builder.Build().RunAsync();

internal sealed class WorkerService(IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(configuration["Tenant:InstanceId"], out var instanceId) || instanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(configuration["Tenant:ConnectionString"]) || string.IsNullOrWhiteSpace(configuration["Executor:Path"]))
            throw new WorkerDockException("WorkerConfigurationUnavailable");
        var ceiling = configuration.GetValue("Worker:HostCeiling", 10);
        var drainSeconds = configuration.GetValue("Worker:DrainSeconds", 40);
        if (drainSeconds is < 1 or > 60) throw new WorkerDockException("WorkerConfigurationUnavailable");
        var boot = Guid.NewGuid().ToString("N");
        var store = new SqlAttemptStore(new(configuration["Tenant:ConnectionString"]!, instanceId));
        var supervisor = new ExecutionSupervisor(new(configuration["Executor:Path"]!,
            configuration.GetSection("Executor:Arguments").Get<string[]>() ?? [],
            configuration.GetSection("Executor:MetadataDigests").Get<Dictionary<string, string>>() ?? []));
        var monitor = new OwnershipMonitor(store, store, supervisor, TimeProvider.System);
        await using var topology = new QuartzTopology(new(configuration["Tenant:ConnectionString"]!, instanceId));
        var execution = await topology.CreateExecutionAsync(new(instanceId, boot, ceiling), new Factory(store, monitor, boot), stoppingToken);
        try
        {
            // Admission fails closed until Task09 registers the boot/capabilities and opens effective policy gates.
            await execution.Start(stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(85));
            try { await execution.Standby(shutdown.Token); }
            finally
            {
                try { await monitor.DrainAsync(TimeSpan.FromSeconds(drainSeconds), shutdown.Token); }
                finally { await execution.Shutdown(false, shutdown.Token); }
            }
        }
    }
    private sealed class Factory(IAttemptStore store, IAttemptRunner runner, string boot) : IJobFactory
    {
        public ValueTask<JobScope> CreateJob(TriggerFiredBundle bundle, IScheduler scheduler, CancellationToken cancellationToken = default)
        {
            if (bundle.JobDetail.JobType != typeof(ExecuteTemplateJob)) throw new SchedulerException("UnsupportedExecutionJob");
            return ValueTask.FromResult(new JobScope(new ExecuteTemplateJob(store, runner, new(boot), TimeProvider.System)));
        }
        public ValueTask ReturnJob(JobScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
