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
using WorkerDock.Runtime.Workers;

namespace WorkerDock.ProcessTests;

public sealed class ExecutionControlTests
{
    [Fact]
    public async Task Direct_dispatch_runs_children_up_to_configured_ceiling()
    {
        var stores = Enumerable.Range(0, 3).Select(_ => new InMemoryAttemptStore(TimeProvider.System,
            FixtureProgram.Descriptor("spawn", "unused"), new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5))).ToArray();
        var queue = new TestQueue(stores.Select(store => store.Intent.ExecutionId).ToArray(), ignoreLimit: true);
        var attempts = new RoutingAttemptStore(stores);
        var runner = new BlockingAttemptRunner();
        var loop = new ExecutionDispatchLoop(queue, attempts, runner, TimeProvider.System, maxChildren: 2);

        Assert.Equal(2, await loop.RunOnceAsync(default));
        await runner.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runner.MaximumActive);

        runner.Release.TrySetResult();
        await WaitUntil(() => runner.Active == 0);
        Assert.Equal(1, await loop.RunOnceAsync(default));
        await runner.ThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runner.MaximumActive);
        runner.Release.TrySetResult();
        await WaitUntil(() => runner.Active == 0);
    }

    [Fact]
    public async Task Direct_dispatch_descriptor_failure_is_recorded_as_pre_entry_failure()
    {
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", "unused"),
            new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5));
        var attempts = new FaultingDescriptorStore(store);
        var runner = new CountingRunner();
        var loop = new ExecutionDispatchLoop(new TestQueue([store.Intent.ExecutionId], repeat: true), attempts, runner, TimeProvider.System, 1);

        Assert.Equal(1, await loop.RunOnceAsync(default));
        await WaitUntil(() => store.Snapshot.State == "Failed");

        Assert.Equal(0, runner.Calls);
        Assert.False(store.Snapshot.EffectsMayHaveOccurred);
    }

    [Fact]
    public async Task Restart_after_possible_handler_entry_requires_review_before_admission()
    {
        var clock = new DispatchClock();
        var store = new InMemoryAttemptStore(clock, FixtureProgram.Descriptor("spawn", "unused"),
            new(null, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, ["Busy"], false, "Queue", 100, 5));
        Assert.NotNull((await store.BeginAsync(store.Intent.ExecutionId, "previous-process", default)).Lease);
        clock.Now += TimeSpan.FromMinutes(2);
        var runner = new CountingRunner();
        var loop = new ExecutionDispatchLoop(new TestQueue([store.Intent.ExecutionId], repeat: true), store, runner, clock, 1);

        Assert.Equal(1, await loop.RunOnceAsync(default));
        await WaitUntil(() => store.Snapshot.State == "NeedsReview");

        Assert.Equal(0, runner.Calls);
        Assert.True(store.Snapshot.EffectsMayHaveOccurred);
    }

    [Fact]
    public async Task Restart_replays_uncertain_attempt_only_when_bounded_policy_allows_it()
    {
        var clock = new DispatchClock();
        var store = new InMemoryAttemptStore(clock, FixtureProgram.Descriptor("spawn", "unused"),
            new(null, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, ["Busy"], true, "Queue", 100, 5));
        Assert.NotNull((await store.BeginAsync(store.Intent.ExecutionId, "previous-process", default)).Lease);
        clock.Now += TimeSpan.FromMinutes(2);
        var runner = new CountingRunner();
        var loop = new ExecutionDispatchLoop(new TestQueue([store.Intent.ExecutionId], repeat: true), store, runner, clock, 1);

        await loop.RunOnceAsync(default);
        await WaitUntil(() => store.Snapshot.State == "RetryWaiting");
        var replay = Assert.Single(store.Retries);
        Assert.Equal(0, runner.Calls);

        clock.Now = replay.DueUtc;
        await loop.RunOnceAsync(default);
        await WaitUntil(() => runner.Calls == 1);
        Assert.Equal(2, store.Snapshot.Ordinal);
    }

    [Fact]
    public async Task Direct_dispatch_retries_only_after_persisted_due_time()
    {
        var clock = new DispatchClock();
        var store = new InMemoryAttemptStore(clock, FixtureProgram.Descriptor("spawn", "unused"),
            new(null, 2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), 0, ["Busy"], false, "Queue", 100, 5));
        var runner = new SequencedCompletionRunner(store);
        var attempts = new SignaledAttemptStore(store, signalOnBegin: 2);
        var loop = new ExecutionDispatchLoop(new TestQueue([store.Intent.ExecutionId], repeat: true), attempts, runner, clock, 2);

        Assert.Equal(1, await loop.RunOnceAsync(default));
        await WaitUntil(() => store.Snapshot.State == "RetryWaiting");
        var retry = Assert.Single(store.Retries);
        Assert.Equal(1, runner.Calls);

        await loop.RunOnceAsync(default);
        await attempts.Signaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runner.Calls);

        clock.Now = retry.DueUtc;
        await loop.RunOnceAsync(default);
        await WaitUntil(() => store.Snapshot.State == "Succeeded");
        Assert.Equal("Succeeded", store.Snapshot.State);
    }

    [Fact]
    public async Task Direct_dispatch_drain_stops_new_admission_and_cancels_active_run()
    {
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", "unused"),
            new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5));
        var runner = new DrainAwareRunner();
        var loop = new ExecutionDispatchLoop(new TestQueue([store.Intent.ExecutionId], repeat: true), store, runner, TimeProvider.System, 1);

        await loop.RunOnceAsync(default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await loop.DrainAsync(TimeSpan.FromSeconds(5), default);

        Assert.True(runner.Canceled.Task.IsCompleted);
        Assert.Equal(0, await loop.RunOnceAsync(default));
    }

    [Fact]
    public async Task Ownership_drain_before_child_start_records_known_no_effects()
    {
        var store = new InMemoryAttemptStore(TimeProvider.System, FixtureProgram.Descriptor("spawn", "unused"),
            new(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5));
        var lease = (await store.BeginAsync(store.Intent.ExecutionId, "boot", default)).Lease!;
        var supervisor = new NeverStartedSupervisor();
        var monitor = new OwnershipMonitor(store, store, supervisor, TimeProvider.System);
        await monitor.DrainAsync(TimeSpan.FromSeconds(1), default);

        await monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), TimeProvider.System.GetTimestamp(), default);

        Assert.Equal(0, supervisor.Calls);
        Assert.Equal("Failed", store.Snapshot.State);
        Assert.False(store.Snapshot.EffectsMayHaveOccurred);
    }

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
        var bytes = WorkerDock.Runtime.Ipc.ExecutionProtocol.Serialize(new WorkerDock.Runtime.Ipc.ExecuteRequest(WorkerDock.Runtime.Ipc.ExecutionProtocol.CurrentVersion, Guid.NewGuid(), long.MaxValue, new string('x', 64), descriptor));
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
        public ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct) => inner.BeginAsync(executionId, workerBootId, ct);
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

    private sealed class TestQueue(Guid[] executionIds, bool repeat = false, bool ignoreLimit = false) : IExecutionQueue
    {
        private int offset;
        public ValueTask<IReadOnlyList<Guid>> ReadReadyAsync(int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var requestedLimit = ignoreLimit ? executionIds.Length : limit;
            if (repeat) return ValueTask.FromResult<IReadOnlyList<Guid>>(executionIds.Take(requestedLimit).ToArray());
            var batch = executionIds.Skip(offset).Take(requestedLimit).ToArray();
            offset += Math.Min(limit, batch.Length);
            return ValueTask.FromResult<IReadOnlyList<Guid>>(batch);
        }
    }

    private sealed class RoutingAttemptStore(InMemoryAttemptStore[] stores) : IAttemptStore
    {
        private InMemoryAttemptStore For(Guid id) => stores.Single(store => store.Intent.ExecutionId == id);
        public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) => For(intent.ExecutionId).BeginAsync(intent, workerBootId, ct);
        public ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct) => For(executionId).BeginAsync(executionId, workerBootId, ct);
        public ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct) => For(lease.ExecutionId).GetDescriptorAsync(lease, ct);
        public ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct) => For(lease.ExecutionId).RenewAsync(lease, ct);
        public ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct) => For(lease.ExecutionId).CompleteAsync(lease, completion, ct);
        public async ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct)
        {
            var reviewed = 0;
            foreach (var store in stores) reviewed += await store.ReviewExpiredAsync(limit, ct);
            return reviewed;
        }
    }

    private sealed class BlockingAttemptRunner : IAttemptRunner
    {
        private int active, maximumActive, starts;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximumActive);
        public TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThreeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release => release;

        public async ValueTask RunAsync(AttemptLease lease, ExecutionDescriptor descriptor, long authorizationStarted, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            while (current > (observed = Volatile.Read(ref maximumActive)) &&
                   Interlocked.CompareExchange(ref maximumActive, current, observed) != observed) { }
            if (current >= 2) TwoStarted.TrySetResult();
            if (Interlocked.Increment(ref starts) >= 3) ThreeStarted.TrySetResult();
            try { await release.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed class FaultingDescriptorStore(IAttemptStore inner) : IAttemptStore
    {
        public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) => inner.BeginAsync(intent, workerBootId, ct);
        public ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct) => inner.BeginAsync(executionId, workerBootId, ct);
        public ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct) => ValueTask.FromException<ExecutionDescriptor>(new InvalidOperationException("descriptor unavailable"));
        public ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct) => inner.RenewAsync(lease, ct);
        public ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct) => inner.CompleteAsync(lease, completion, ct);
        public ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct) => inner.ReviewExpiredAsync(limit, ct);
    }

    private sealed class SignaledAttemptStore(IAttemptStore inner, int signalOnBegin) : IAttemptStore
    {
        private int begins;
        public TaskCompletionSource Signaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) => inner.BeginAsync(intent, workerBootId, ct);
        public async ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct)
        {
            var result = await inner.BeginAsync(executionId, workerBootId, ct);
            if (Interlocked.Increment(ref begins) == signalOnBegin) Signaled.TrySetResult();
            return result;
        }
        public ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct) => inner.GetDescriptorAsync(lease, ct);
        public ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct) => inner.RenewAsync(lease, ct);
        public ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct) => inner.CompleteAsync(lease, completion, ct);
        public ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct) => inner.ReviewExpiredAsync(limit, ct);
    }

    private sealed class CountingRunner : IAttemptRunner
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ValueTask RunAsync(AttemptLease lease, ExecutionDescriptor descriptor, long authorizationStarted, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverStartedSupervisor : IExecutionSupervisor
    {
        public int Calls { get; private set; }
        public ValueTask<IExecutionChild> StartAsync(Guid attemptId, long generation, ExecutionDescriptor descriptor, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromException<IExecutionChild>(new InvalidOperationException("supervisor should not be called"));
        }
    }

    private sealed class SequencedCompletionRunner(InMemoryAttemptStore store) : IAttemptRunner
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public async ValueTask RunAsync(AttemptLease lease, ExecutionDescriptor descriptor, long authorizationStarted, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref calls);
            var result = call == 1
                ? new TemplateResult("TransientFailure", "Busy", false, System.Text.Json.JsonSerializer.SerializeToElement(new { }), null)
                : new TemplateResult("Success", "Ok", false, System.Text.Json.JsonSerializer.SerializeToElement(new { }), null);
            await store.CompleteAsync(lease, new(result, "Completed", true, false), ct);
        }
    }

    private sealed class DrainAwareRunner : IAttemptRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask RunAsync(AttemptLease lease, ExecutionDescriptor descriptor, long authorizationStarted, CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { Canceled.TrySetResult(); }
        }
    }

    private sealed class DispatchClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkerDockControl_" + Guid.NewGuid().ToString("N"));
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
