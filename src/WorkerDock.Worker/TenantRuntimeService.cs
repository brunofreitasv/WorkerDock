using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Alerts;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Retention;
using WorkerDock.Infrastructure.Runtime;
using WorkerDock.Infrastructure.Scheduling;
using WorkerDock.Infrastructure.Settings;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Runtime.Ownership;
using WorkerDock.Runtime.Processes;
using WorkerDock.Runtime.Workers;

namespace WorkerDock.Worker;

public sealed class TenantRuntimeService(IConfiguration configuration, ISecretVault secrets) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !Guid.TryParse(configuration["Tenant:InstanceId"], out var instanceId) || instanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(configuration["Executor:Path"]) ||
            configuration["Worker:CatalogDigest"] is not { Length: 64 } catalogDigest)
            throw new WorkerDockException("WorkerConfigurationUnavailable");
        var ceiling = configuration.GetValue("Worker:HostCeiling", 10);
        var drainSeconds = configuration.GetValue("Worker:DrainSeconds", 40);
        if (ceiling is < 1 or > 1000 || drainSeconds is < 1 or > 60)
            throw new WorkerDockException("WorkerConfigurationUnavailable");

        using var tenantGuard = RuntimeMutex.Acquire(instanceId);
        TenantSqlConfiguration.RejectLegacy(configuration, "Tenant:ConnectionString");
        var connection = await TenantSqlConnectionResolver.ResolveAsync(
            TenantSqlConfiguration.Read(configuration, "Tenant:Sql"), secrets, stoppingToken);
        var database = new TenantDatabaseOptions(connection, instanceId);
        var bootId = Guid.NewGuid();
        var status = new SqlRuntimeStatus(database, ceiling);
        await using var bufferedEvents = new BufferedExecutionEvents(new SqlExecutionEvents(database));

        IReadOnlyList<TemplateDescriptor> installedDescriptors =
        [
            new WorkerDock.Templates.SqlServer.SqlTemplate("1.1.0").Descriptor,
            WorkerDock.Templates.Http.HttpTemplateDescriptors.Get("1.1.0")
        ];
        if (TemplateMetadata.AggregateDigest(installedDescriptors) != catalogDigest)
            throw new WorkerDockException("CatalogMismatch");
        var catalog = new SqlTemplateCatalog(database, installedDescriptors);
        await catalog.RegisterAsync(installedDescriptors, stoppingToken);

        var attempts = new SqlAttemptStore(database);
        var supervisor = new ExecutionSupervisor(new(
            configuration["Executor:Path"]!,
            configuration.GetSection("Executor:Arguments").Get<string[]>() ?? [],
            installedDescriptors.ToDictionary(x => x.Id + "@" + x.Version, x => x.MetadataDigest, StringComparer.Ordinal)),
            bufferedEvents);
        var monitor = new OwnershipMonitor(attempts, attempts, supervisor, TimeProvider.System);
        var dispatch = new ExecutionDispatchLoop(new SqlExecutionQueue(database), attempts, monitor, TimeProvider.System, ceiling);
        var schedules = new SqlScheduleSweep(database, new OccurrenceWriter(database, new FiveFieldScheduleEvaluator(), catalog));
        var alertEvaluator = new IncidentEvaluator(database);
        var deliveries = new SqlAlertDeliveryStore(database);
        var deliveryRunner = new AlertDeliveryRunner(deliveries,
            new IAlertSink[] { new WebhookAlertSink(new SqlIntegrationSettings(database)) }, secrets, bootId);
        var retention = new SqlRetentionService(database, configuration["Retention:ArchiveRoot"]);
        var loops = new RuntimeLoops(token => status.HeartbeatAsync(bootId, true, token));

        try
        {
            await loops.PulseAsync(stoppingToken);
            await loops.RunAsync(
            [
                new(TimeSpan.FromSeconds(1), async token => { await schedules.RunDueAsync(DateTimeOffset.UtcNow, 100, token); }),
                new(TimeSpan.FromMilliseconds(500), async token => { await dispatch.RunOnceAsync(token); }),
                new(TimeSpan.FromSeconds(5), async token => { await alertEvaluator.EvaluateAsync(DateTimeOffset.UtcNow, 100, token); }),
                new(TimeSpan.FromSeconds(2), async token =>
                {
                    foreach (var due in await deliveries.ReadDueAsync(20, token))
                        await deliveryRunner.RunAsync(due, token);
                }),
                new(TimeSpan.FromSeconds(5), async token => { await MetricAggregation.AggregateAsync(database, DateTimeOffset.UtcNow, 100, token); }),
                new(TimeSpan.FromMinutes(1), async token => { await schedules.RefreshZoneRulesAsync(100, token); }),
                new(TimeSpan.FromMinutes(1), async token => { await retention.RunBatchAsync(DateTimeOffset.UtcNow, 100, token); })
            ], stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(drainSeconds + 5));
            try { await status.HeartbeatAsync(bootId, false, shutdown.Token); }
            catch { /* Stale heartbeat becomes unavailable after its freshness window. */ }
            await dispatch.DrainAsync(TimeSpan.FromSeconds(drainSeconds), shutdown.Token);
        }
    }
}
