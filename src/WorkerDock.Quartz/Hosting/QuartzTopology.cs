using global::Quartz;
using global::Quartz.Impl;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;

namespace WorkerDock.Quartz.Hosting;

/// <summary>Owns scheduler factories; dispose after shutting down caller-owned schedulers.</summary>
public sealed class QuartzTopology(QuartzConnections connections) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<StandaloneSchedulerFactory> factories = [];
    private readonly Dictionary<string, IScheduler> memorySchedulers = [];
    private bool memory;
    private bool disposed;
    private QuartzNodeOptions? memoryNode;

    public static QuartzTopology CreateMemory(Guid instanceId, string environment)
    {
        if (environment != "Development") throw new WorkerDockException("MemoryStoreDevelopmentOnly");
        if (instanceId == Guid.Empty) throw new WorkerDockException("TenantInstanceMismatch");
        return new(new("", instanceId)) { memory = true };
    }

    public ValueTask<IScheduler> CreateScheduleAsync(QuartzNodeOptions options, CancellationToken ct) => CreateAsync(options, false, false, ct);
    public ValueTask<IScheduler> CreateScheduleAsync(QuartzNodeOptions options, global::Quartz.Extensibility.IJobFactory jobFactory, CancellationToken ct) => CreateAsync(options, false, false, ct, jobFactory);
    public ValueTask<IScheduler> CreateExecutionAsync(QuartzNodeOptions options, CancellationToken ct) => CreateAsync(options, true, false, ct);
    public ValueTask<IScheduler> CreateExecutionAsync(QuartzNodeOptions options, global::Quartz.Extensibility.IJobFactory jobFactory, CancellationToken ct) => CreateAsync(options, true, false, ct, jobFactory);
    public ValueTask<IScheduler> CreateExecutionPublisherAsync(QuartzNodeOptions options, CancellationToken ct) => CreateAsync(options, true, true, ct);

    private async ValueTask<IScheduler> CreateAsync(QuartzNodeOptions options, bool execution, bool publisher, CancellationToken ct, global::Quartz.Extensibility.IJobFactory? jobFactory = null)
    {
        if (options.InstanceId == Guid.Empty || options.InstanceId != connections.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
        if (string.IsNullOrWhiteSpace(options.BootId) || options.BootId.Length > 150 || options.BootId is "*" or "NON_CLUSTERED" || options.HostCeiling is < 1 or > 1024)
            throw new WorkerDockException("InvalidQuartzNode");
        var limits = options.TemplateLimits.ToArray();
        if (limits.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 200 || pair.Key.Contains('*') || pair.Key == "_" || pair.Value < 0))
            throw new WorkerDockException("InvalidQuartzLimit");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (memory)
            {
                if (memoryNode is not null && (memoryNode.BootId != options.BootId || memoryNode.HostCeiling != options.HostCeiling ||
                    memoryNode.TemplateLimits.Count != limits.Length ||
                    limits.Any(pair => !memoryNode.TemplateLimits.TryGetValue(pair.Key, out var value) || value != pair.Value)))
                    throw new WorkerDockException("MemoryStoreSingleNodeOnly");
                memoryNode ??= options;
            }
            else await connections.VerifyAsync(options.InstanceId, ct);
            var name = execution ? "WorkerDock.Execute" : "WorkerDock.Schedule";
            if (memory && memorySchedulers.TryGetValue(name, out var existing))
                return publisher ? PublisherScheduler.Create(existing, false) : existing;
            var factory = QuartzSchedulerBuilder.Create(builder =>
            {
                if (jobFactory is not null) builder.UseJobFactory(jobFactory);
                builder.ConfigureScheduler(settings =>
                {
                    settings.InstanceName = name;
                    settings.InstanceId = options.BootId + (publisher && !memory ? ".publisher" : "");
                    settings.MaxBatchSize = options.HostCeiling;
                });
                if (publisher && !memory) builder.UseThreadPool<ZeroSizeThreadPool>();
                else builder.UseDefaultThreadPool(options.HostCeiling);
                builder.UseExecutionLimits(configuration =>
                {
                    var scope = memory ? ExecutionLimitScope.Node : ExecutionLimitScope.Cluster;
                    configuration.ForDefaultGroup(options.StartClosed ? 0 : 1, scope).ForOtherGroups(options.StartClosed ? 0 : 1, scope);
                    foreach (var pair in limits) configuration.ForGroup(pair.Key, pair.Value, scope);
                });
                if (memory) builder.UseInMemoryStore();
                else builder.UsePersistentStore(store =>
                {
                    var settings = new SqlConnectionStringBuilder(connections.ConnectionString);
                    settings.ConnectTimeout = Math.Clamp(settings.ConnectTimeout, 1, 10);
                    store.UseSqlServer(SqlClientFactory.Instance, settings.ConnectionString);
                    store.UseSystemTextJsonSerializer(json => json.AddCalendarSerializer(new Scheduling.WorkerDockPolicyCalendarSerializer()));
                    store.UseClustering();
                    store.ConfigureStore(settings => ConfigurePersistentStore(settings, execution));
                });
            }).Build();
            try
            {
                var scheduler = await factory.GetScheduler(ct);
                factories.Add(factory);
                if (memory) memorySchedulers.Add(name, scheduler);
                return publisher ? PublisherScheduler.Create(scheduler, !memory) : scheduler;
            }
            catch
            {
                await factory.DisposeAsync();
                throw;
            }
        }
        catch (SchedulerException) { throw new WorkerDockException("QuartzStoreUnavailable"); }
        finally { gate.Release(); }
    }

    internal static void ConfigurePersistentStore(AdoJobStoreOptions settings, bool execution)
    {
        settings.TablePrefix = execution ? "[workerdock].WDE_QRTZ_" : "[workerdock].WDS_QRTZ_";
        settings.StoreJobDataAsStrings = true;
        settings.AcquireTriggersWithinLock = true;
        settings.SchemaProvisioning = SchemaProvisioning.Validate;
        settings.CommandTimeout = TimeSpan.FromSeconds(10);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            foreach (var factory in factories.AsEnumerable().Reverse()) await factory.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
