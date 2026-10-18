using System.Diagnostics;
using Quartz;
using Quartz.Extensibility;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Execution;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;
using WorkerDock.Runtime.Ownership;

namespace WorkerDock.ProcessTests;

public sealed class ExecutionControlTests
{
    [Fact]
    public void Accepted_near_limit_configuration_leaves_room_for_pinned_settings_and_full_envelope()
    {
        var descriptor = FixtureProgram.Descriptor("spawn", "unused") with
        {
            AttemptOrdinal = int.MaxValue, IdempotencyKey = new string('x', 100), ProfileRevisionId = Guid.NewGuid(),
            Configuration = System.Text.Json.JsonSerializer.SerializeToElement(new { value = new string('x', 54000) }),
            ProfileConfiguration = System.Text.Json.JsonSerializer.SerializeToElement(new { value = new string('x', 4000) }),
            IntegrationConfiguration = System.Text.Json.JsonSerializer.SerializeToElement(new { value = new string('x', 4000) }),
            DeadlineUtc = DateTimeOffset.MaxValue
        };
        Assert.True(ExecutionPayloadLimits.FitsJobConfiguration(descriptor.TemplateId, descriptor.TemplateVersion, descriptor.Configuration));
        var bytes = WorkerDock.Runtime.Ipc.ExecutionProtocol.Serialize(new WorkerDock.Runtime.Ipc.ExecuteRequest(1, Guid.NewGuid(), long.MaxValue, new string('x', 64), descriptor));
        Assert.InRange(bytes.Length, 62000, WorkerDock.Runtime.Ipc.ExecutionProtocol.MaximumMessageBytes);
    }

    [Fact]
    public async Task Ambiguous_startup_failure_preserves_uncertainty()
    {
        var descriptor = FixtureProgram.Descriptor("spawn", "unused");
        var store = new InMemoryAttemptStore(TimeProvider.System, descriptor, new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5));
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var monitor = new OwnershipMonitor(store, store, new AmbiguousSupervisor(), TimeProvider.System);
        await monitor.RunAsync(lease, descriptor, TimeProvider.System.GetTimestamp(), default);
        Assert.Equal("NeedsReview", store.Snapshot.State); Assert.True(store.Snapshot.EffectsMayHaveOccurred);
    }
    private sealed class AmbiguousSupervisor : IExecutionSupervisor
    {
        public ValueTask<IExecutionChild> StartAsync(Guid attemptId, long generation, ExecutionDescriptor descriptor, CancellationToken ct) =>
            ValueTask.FromException<IExecutionChild>(new WorkerDock.Runtime.Processes.ExecutionProcessException("ExecutorStartupFailed"));
    }

    [Fact]
    public async Task Oversized_prelaunch_request_fails_with_known_no_effects_and_safe_code()
    {
        var descriptor = FixtureProgram.Descriptor("spawn", "secret-must-not-leak") with
        { Configuration = System.Text.Json.JsonSerializer.SerializeToElement(new { secret = new string('x', 65500) }) };
        var rejection = await Assert.ThrowsAsync<WorkerDock.Runtime.Processes.ExecutionProcessException>(async () =>
            await FixtureProgram.Supervisor().StartAsync(Guid.NewGuid(), 1, descriptor, default));
        Assert.True(rejection.NoExternalEffects); Assert.Equal("IpcMessageTooLarge", rejection.SafeCode);
        Assert.DoesNotContain("secret-must-not-leak", rejection.ToString());
        var store = new InMemoryAttemptStore(TimeProvider.System, descriptor, new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5));
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var monitor = new OwnershipMonitor(store, store, FixtureProgram.Supervisor(), TimeProvider.System);
        await monitor.RunAsync(lease, descriptor, TimeProvider.System.GetTimestamp(), default);
        Assert.Equal("Failed", store.Snapshot.State);
        Assert.False(store.Snapshot.EffectsMayHaveOccurred);
        Assert.False(store.Snapshot.PermitRetained);
        Assert.Empty(store.Retries);
    }

    [Theory]
    [InlineData(1999, "Succeeded")]
    [InlineData(2000, "TimedOut")]
    [InlineData(2001, "TimedOut")]
    public async Task Success_observed_around_deadline_while_control_poll_is_held_obeys_timeout(int completionMilliseconds, string expected)
    {
        var policy = new ExecutionPolicy(TimeSpan.FromSeconds(2), 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5);
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", "unused"), policy);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var clock = new ControlledClock(); var child = new ControlledChild(); var signals = new HeldSignals(store);
        var monitor = new OwnershipMonitor(store, signals, new ControlledSupervisor(child), clock);
        var run = monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), clock.GetTimestamp(), default).AsTask();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMilliseconds(completionMilliseconds - 1000));
            child.Succeed(); signals.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, store.Snapshot.State);
            Assert.Equal(expected, store.Snapshot.AttemptState);
        }
        finally { signals.Release.TrySetResult(); child.Succeed(); await monitor.DrainAsync(TimeSpan.FromMilliseconds(100), default); await run; }
    }

    [Theory]
    [InlineData(46)]
    [InlineData(56)]
    public async Task Renewal_ack_held_across_previous_safety_cutoff_contains_the_real_process_tree(int acknowledgementSeconds)
    {
        using var fixture = new DirectoryFixture();
        var policy = new ExecutionPolicy(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5);
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", fixture.Path), policy);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var clock = new ControlledClock(); var held = new HeldRenewalStore(store);
        var monitor = new OwnershipMonitor(held, store, FixtureProgram.Supervisor(), clock);
        var run = monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), clock.GetTimestamp(), default).AsTask();
        var pids = await ContainmentTests.WaitForPids(fixture.Path);
        try
        {
            // Enter renewal while the prior conservative cutoff is still valid, then withhold its committed acknowledgement.
            clock.Advance(TimeSpan.FromSeconds(40));
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(acknowledgementSeconds - 40));
            held.Release.TrySetResult();
            await WaitUntil(() => pids.All(pid => !Alive(pid)), 5);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("NeedsReview", store.Snapshot.State);
            Assert.Empty(store.Retries);
        }
        finally { held.Release.TrySetResult(); await monitor.DrainAsync(TimeSpan.FromMilliseconds(100), default); await run; }
    }

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

    private sealed class ControlledClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
    }
    private sealed class HeldSignals(IAttemptSignals inner) : IAttemptSignals
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AttemptSignal> ReadSignalAsync(AttemptLease lease, CancellationToken ct)
        {
            var signal = await inner.ReadSignalAsync(lease, ct);
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return signal;
        }
    }
    private sealed class HeldRenewalStore(IAttemptStore inner) : IAttemptStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) => inner.BeginAsync(intent, workerBootId, ct);
        public ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct) => inner.GetDescriptorAsync(lease, ct);
        public async ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct)
        {
            var renewed = await inner.RenewAsync(lease, ct);
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return renewed;
        }
        public ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct) => inner.CompleteAsync(lease, completion, ct);
        public ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct) => inner.ReviewExpiredAsync(limit, ct);
    }
    private sealed class ControlledSupervisor(IExecutionChild child) : IExecutionSupervisor
    {
        public ValueTask<IExecutionChild> StartAsync(Guid attemptId, long generation, ExecutionDescriptor descriptor, CancellationToken ct) => ValueTask.FromResult(child);
    }
    private sealed class ControlledChild : IExecutionChild
    {
        private readonly TaskCompletionSource<TemplateResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Succeed() => result.TrySetResult(new("Success", "Ok", false, System.Text.Json.JsonSerializer.SerializeToElement(new { }), null));
        public ValueTask<TemplateResult> WaitAsync(CancellationToken ct) => new(result.Task.WaitAsync(ct));
        public ValueTask CancelAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask TerminateAsync(CancellationToken ct) { Succeed(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkerDockControl_" + Guid.NewGuid().ToString("N"));
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
