using Quartz;

using WorkerDock.Application.Common;
using WorkerDock.Quartz.Hosting;

namespace WorkerDock.IntegrationTests;

public sealed class QuartzTopologyTests
{
    [Fact]
    public async Task Memory_publisher_delivers_to_execution_only_and_cannot_start()
    {
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var node = new QuartzNodeOptions(id, Guid.NewGuid().ToString("N"), 2);
        var schedule = await topology.CreateScheduleAsync(node, default);
        var execution = await topology.CreateExecutionAsync(node, default);
        var publisher = await topology.CreateExecutionPublisherAsync(node, default);
        Assert.Equal("WorkerDock.Schedule", schedule.SchedulerName);
        Assert.Equal("WorkerDock.Execute", publisher.SchedulerName);
        Assert.Equal(SchedulerStatus.Created, publisher.Status);
        await Assert.ThrowsAsync<WorkerDockException>(async () => await publisher.Start());
        var delivery = Guid.NewGuid().ToString("N");
        var completion = MemoryProbe.Expect(delivery);
        await publisher.ScheduleJob(JobBuilder.Create<MemoryProbe>().WithIdentity(delivery)
            .UsingJobData("ExecutionId", Guid.NewGuid().ToString("N"))
            .UsingJobData("DeliveryId", delivery).UsingJobData("AttemptOrdinal", "1").Build(),
            TriggerBuilder.Create().WithIdentity(delivery).StartNow().Build());
        Assert.Empty(await schedule.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
        await execution.Start();
        var keys = await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "AttemptOrdinal", "DeliveryId", "ExecutionId" }, keys.Order().ToArray());
        Assert.Equal(SchedulerStatus.Created, publisher.Status);
        await publisher.Shutdown();
        Assert.NotEqual(SchedulerStatus.Shutdown, execution.Status);
    }

    [Fact]
    public async Task Memory_is_disposable_and_refuses_another_node_or_tenant()
    {
        var id = Guid.NewGuid();
        var node = new QuartzNodeOptions(id, "boot-a", 1);
        await using (var topology = QuartzTopology.CreateMemory(id, "Development"))
        {
            var execution = await topology.CreateExecutionAsync(node, default);
            await execution.AddJob(JobBuilder.Create<MemoryProbe>().WithIdentity("ephemeral").StoreDurably().Build());
            await Assert.ThrowsAsync<WorkerDockException>(async () => await topology.CreateExecutionAsync(node with { BootId = "boot-b" }, default));
            await Assert.ThrowsAsync<WorkerDockException>(async () => await topology.CreateExecutionPublisherAsync(
                node with { TemplateLimits = new Dictionary<string, int> { ["template-probe"] = 2 } }, default));
            await Assert.ThrowsAsync<WorkerDockException>(async () => await topology.CreateScheduleAsync(node with { InstanceId = Guid.NewGuid() }, default));
        }
        await using var fresh = QuartzTopology.CreateMemory(id, "Development");
        Assert.Null(await (await fresh.CreateExecutionAsync(node, default)).GetJobDetail(new("ephemeral")));
        Assert.Throws<WorkerDockException>(() => QuartzTopology.CreateMemory(id, "Production"));
    }

    [Fact]
    public async Task Simultaneous_memory_tenants_do_not_share_execution_jobs()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await using var first = QuartzTopology.CreateMemory(firstId, "Development");
        await using var second = QuartzTopology.CreateMemory(secondId, "Development");
        var firstScheduler = await first.CreateExecutionAsync(new(firstId, "first", 1), default);
        var secondScheduler = await second.CreateExecutionAsync(new(secondId, "second", 1), default);
        await firstScheduler.AddJob(JobBuilder.Create<MemoryProbe>().WithIdentity("first-only").StoreDurably().Build());
        Assert.Null(await secondScheduler.GetJobDetail(new("first-only")));
    }

    [Theory]
    [InlineData("Secret", "value")]
    [InlineData("AttemptOrdinal", 1)]
    public async Task Publisher_rejects_non_opaque_or_non_string_data(string key, object value)
    {
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var publisher = await topology.CreateExecutionPublisherAsync(new(id, "boot", 1), default);
        var job = JobBuilder.Create<MemoryProbe>().WithIdentity("invalid").Build();
        job.JobDataMap[key] = value;
        await Assert.ThrowsAsync<WorkerDockException>(async () => await publisher.ScheduleJob(job, TriggerBuilder.Create().StartNow().Build()));
    }

    [Fact]
    public async Task Unavailable_sql_fails_closed_without_driver_details()
    {
        var id = Guid.NewGuid();
        await using var topology = new QuartzTopology(new("Server=127.0.0.1,1;User ID=unused;Password=not-a-secret;Connect Timeout=1;Encrypt=true", id));
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await topology.CreateExecutionPublisherAsync(new(id, "boot", 1), default));
        Assert.Equal("PersistenceUnavailable", error.Code);
        Assert.Null(error.InnerException);
    }
}

public sealed class MemoryProbe : IJob
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string[]>> Pending = new();
    public static Task<string[]> Expect(string delivery)
    {
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(Pending.TryAdd(delivery, completion));
        return completion.Task;
    }
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var delivery = context.MergedJobDataMap.GetString("DeliveryId")!;
        if (Pending.TryRemove(delivery, out var completion)) completion.TrySetResult(context.MergedJobDataMap.Keys.ToArray());
        return ValueTask.CompletedTask;
    }
}
