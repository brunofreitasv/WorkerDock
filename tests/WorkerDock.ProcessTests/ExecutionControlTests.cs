using System.Diagnostics;
using Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;
using WorkerDock.Runtime.Ownership;

namespace WorkerDock.ProcessTests;

public sealed class ExecutionControlTests
{
    [Fact]
    public async Task Canceled_shutdown_still_contains_remaining_children()
    {
        using var fixture = new DirectoryFixture();
        var policy = new ExecutionPolicy(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5);
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", fixture.Path), policy);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var monitor = new OwnershipMonitor(store, store, FixtureProgram.Supervisor(), TimeProvider.System);
        var run = monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), TimeProvider.System.GetTimestamp(), default).AsTask();
        var pids = await ContainmentTests.WaitForPids(fixture.Path);
        try
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await monitor.DrainAsync(TimeSpan.FromSeconds(1), canceled.Token));
            Assert.All(pids, pid => Assert.False(Alive(pid)));
            await run;
        }
        finally { await monitor.DrainAsync(TimeSpan.FromMilliseconds(100), default); await run; }
    }

    [Fact]
    public async Task Renewal_ack_after_local_safety_deadline_never_extends_child_lifetime()
    {
        using var fixture = new DirectoryFixture();
        var policy = new ExecutionPolicy(null, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, [], false, "Queue", 100, 5);
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", fixture.Path), policy);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var clock = new MonotonicClock();
        var monitor = new OwnershipMonitor(store, store, FixtureProgram.Supervisor(), clock);
        var run = monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), clock.GetTimestamp(), default).AsTask();
        var pids = await ContainmentTests.WaitForPids(fixture.Path);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(56));
            await WaitUntil(() => pids.All(pid => !Alive(pid)), 8);
            await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("NeedsReview", store.Snapshot.State);
        }
        finally { await monitor.DrainAsync(TimeSpan.FromMilliseconds(100), default); await run; }
    }

    [Fact]
    public async Task Finite_timeout_allows_thirty_second_grace_then_contains_the_process_tree()
    {
        using var fixture = new DirectoryFixture();
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(2), 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, [], false, "Queue", 100, 5);
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", fixture.Path), policy);
        var started = TimeProvider.System.GetTimestamp();
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var monitor = new OwnershipMonitor(store, store, FixtureProgram.Supervisor(), TimeProvider.System);
        var run = monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), started, default).AsTask();
        var pids = await ContainmentTests.WaitForPids(fixture.Path);
        await run.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.InRange(TimeProvider.System.GetElapsedTime(started), TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(45));
        Assert.Equal("TimedOut", store.Snapshot.State);
        Assert.All(pids, pid => Assert.False(Alive(pid)));
    }

    [Fact]
    public async Task Infinite_attempt_stops_while_all_Quartz_slots_are_busy()
    {
        using var first = new DirectoryFixture(); using var second = new DirectoryFixture();
        var policy = new ExecutionPolicy(null, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, [], false, "Queue", 100, 5);
        var stores = new[] { first, second }.Select(f => new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", f.Path), policy)).ToArray();
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var execution = await topology.CreateExecutionAsync(new(id, "control-test", 2) { TemplateLimits = new Dictionary<string, int> { ["spawn"] = 2 } }, new Factory(stores), default);
        try
        {
            foreach (var store in stores) await new QuartzPublisher(execution, typeof(ExecuteTemplateJob)).PublishAsync(store.Intent, default);
            await execution.Start();
            await WaitUntil(() => stores.All(s => s.Snapshot.State == "Running"));
            var pids = await ContainmentTests.WaitForPids(first.Path); var siblings = await ContainmentTests.WaitForPids(second.Path);
            var originalExpiries = stores.Select(s => s.CurrentLease!.ExpiresUtc).ToArray();
            await Task.Delay(TimeSpan.FromSeconds(11));
            for (var i = 0; i < stores.Length; i++)
            {
                Assert.Null(stores[i].CurrentLease!.DeadlineUtc);
                Assert.True(stores[i].CurrentLease!.ExpiresUtc > originalExpiries[i], "Infinite attempts must keep renewing");
            }
            Assert.All(stores, s => Assert.Equal("Running", s.Snapshot.State));
            await stores[0].StopAsync(stores[0].Intent.ExecutionId, true, "force confirmed", new("operator", "Anonymous", Guid.NewGuid()), default);
            await WaitUntil(() => stores[0].Snapshot.State == "Stopped");
            Assert.All(pids, pid => Assert.False(Alive(pid)));
            Assert.All(siblings, pid => Assert.True(Alive(pid)));
            Assert.Equal("Running", stores[1].Snapshot.State);
            Assert.Empty(stores[0].Retries);
            await stores[1].StopAsync(stores[1].Intent.ExecutionId, true, "cleanup", new("operator", "Anonymous", Guid.NewGuid()), default);
            await WaitUntil(() => stores[1].Snapshot.State == "Stopped");
        }
        finally { using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await execution.Shutdown(false, shutdown.Token); }
    }

    private sealed class Factory(InMemoryAttemptStore[] stores) : IJobFactory
    {
        public ValueTask<JobScope> CreateJob(TriggerFiredBundle bundle, IScheduler scheduler, CancellationToken cancellationToken = default)
        {
            var id = Guid.Parse(bundle.JobDetail.JobDataMap.GetString("ExecutionId")!);
            var store = stores.Single(s => s.Intent.ExecutionId == id);
            return ValueTask.FromResult(new JobScope(new ExecuteTemplateJob(store,
                new OwnershipMonitor(store, store, FixtureProgram.Supervisor(), TimeProvider.System), new("control-test"), TimeProvider.System)));
        }
        public ValueTask ReturnJob(JobScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task WaitUntil(Func<bool> predicate, int seconds = 15)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(25);
        Assert.True(predicate(), "Execution control did not make progress independently of Quartz slots");
    }

    private sealed class MonotonicClock : TimeProvider
    {
        private long offset;
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Read(ref offset);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(-100);
        public void Advance(TimeSpan value) => Interlocked.Add(ref offset, (long)(value.TotalSeconds * Stopwatch.Frequency));
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkerDockControl_" + Guid.NewGuid().ToString("N"));
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
