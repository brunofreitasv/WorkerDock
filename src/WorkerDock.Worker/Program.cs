using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Templates;
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
using WorkerDock.Runtime.Workers;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Workers;
using WorkerDock.Quartz.Capacity;

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
        if (!Guid.TryParse(configuration["Worker:InstallationId"], out var installationId) || installationId == Guid.Empty ||
            configuration["Worker:CatalogDigest"] is not { Length: 64 } catalogDigest)
            throw new WorkerDockException("WorkerConfigurationUnavailable");
        var ceiling = configuration.GetValue("Worker:HostCeiling", 10);
        var drainSeconds = configuration.GetValue("Worker:DrainSeconds", 40);
        if (drainSeconds is < 1 or > 60) throw new WorkerDockException("WorkerConfigurationUnavailable");
        var boot = Guid.NewGuid().ToString("N");
        var database = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(configuration["Tenant:ConnectionString"]!, instanceId);
        // Explicit installed descriptors only. Built-in packages are added here with their reviewed handler references.
        IReadOnlyList<TemplateDescriptor> installedDescriptors=[new WorkerDock.Templates.SqlServer.SqlTemplate().Descriptor];
        var registeredDigest=TemplateMetadata.AggregateDigest(installedDescriptors);
        if(registeredDigest!=catalogDigest) throw new WorkerDockException("CatalogMismatch");
        await new SqlTemplateCatalog(database,installedDescriptors).RegisterAsync(installedDescriptors,stoppingToken);
        var capacity = new SqlCapacityStore(database);
        var store = new SqlAttemptStore(database);
        var supervisor = new ExecutionSupervisor(new(configuration["Executor:Path"]!,
            configuration.GetSection("Executor:Arguments").Get<string[]>() ?? [],
            installedDescriptors.ToDictionary(x=>x.Id+"@"+x.Version,x=>x.MetadataDigest,StringComparer.Ordinal)));
        var monitor = new OwnershipMonitor(store, store, supervisor, TimeProvider.System);
        await using var topology = new QuartzTopology(new(configuration["Tenant:ConnectionString"]!, instanceId));
        var initial = await capacity.TransactAsync((s, _) => s.Snapshot, stoppingToken);
        var execution = await topology.CreateExecutionAsync(new(instanceId, boot, ceiling)
            { StartClosed = true, TemplateLimits = initial.TemplateLimits.ToDictionary(x => x.Key, _ => 0) }, new Factory(store, monitor, boot), stoppingToken);
        var coordinator = new CapacityCoordinator(capacity, execution, installationId, boot);
        try
        {
            // Zero limits allow Quartz check-in/recovery without acquiring business work during registration.
            await execution.Start(stoppingToken); await execution.Standby(stoppingToken);
            while (true)
            {
                var prior = await capacity.TransactAsync((state, now) =>
                {
                    if (!state.Workers.TryGetValue(installationId,out var worker)) return (CapacityWorker?)null;
                    if (worker.BootId != boot && worker.State != "Fenced" && worker.HeartbeatUtc <= now.AddSeconds(-60)) state.Fence(installationId,now);
                    return state.Workers[installationId];
                }, stoppingToken);
                if (prior is not null && prior.BootId != boot)
                {
                    var nodes = await execution.QueryClusterNodes(stoppingToken);
                    var firings = await execution.QueryFireInstances(new FireInstanceQuery { SchedulerInstanceId=prior.BootId,State=null,Take=1 },stoppingToken);
                    if (prior.State != "Fenced" || nodes.Any(n => n.InstanceId==prior.BootId) || firings.Items.Count != 0)
                    { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); continue; }
                    await capacity.TransactAsync((state,_) => { state.Recover(installationId,prior.BootId); return true; },stoppingToken);
                }
                await new WorkerRegistration(capacity).RegisterAsync(installationId,boot,catalogDigest,ceiling,stoppingToken); break;
            }
            // Policy/heartbeat polling runs outside Quartz's child execution pool, including infinite jobs.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await coordinator.ApplyPendingAsync(stoppingToken); }
                catch (WorkerDockException) { await execution.Standby(stoppingToken); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
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
