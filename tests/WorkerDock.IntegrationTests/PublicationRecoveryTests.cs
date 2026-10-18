using System.Reflection;
using System.Collections.Concurrent;
using Quartz.Extensibility;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;

namespace WorkerDock.IntegrationTests;

public sealed class PublicationRecoveryTests
{
    [Theory]
    [InlineData("BeforeWrite")]
    [InlineData("AfterWrite")]
    [InlineData("BeforeAck")]
    [InlineData("AfterAck")]
    public async Task Crash_after_store_write_before_ack_does_not_duplicate_authorized_run(string fault)
    {
        await using var fixture = await Fixture.Create();
        fixture.Fault = fault;
        await fixture.Reconcile();
        fixture.Fault = null;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        await fixture.Reconcile();
        Assert.Single(await fixture.Execution.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
        Assert.Single(await fixture.Execution.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()));
        Assert.True(fixture.Store.Acknowledged);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    [InlineData("TimedOut")]
    [InlineData("Skipped")]
    [InlineData("Running")]
    [InlineData("NeedsReview")]
    public async Task Consumed_trigger_is_not_proof_of_missing_execution(string state)
    {
        await using var fixture = await Fixture.Create();
        fixture.Fault = "BeforeAck";
        await fixture.Reconcile();
        await fixture.Execution.UnscheduleJob(QuartzPublisher.TriggerKey(fixture.Intent));
        fixture.Store.State = state;
        fixture.Store.AttemptExists = state == "Running";
        fixture.Fault = null;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        await fixture.Reconcile();
        Assert.Empty(await fixture.Execution.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()));
        Assert.True(fixture.Store.Acknowledged);
    }

    [Fact]
    public async Task Concurrent_publishers_preserve_one_identity_and_ignore_priority_aging()
    {
        await using var fixture = await Fixture.Create();
        var first = fixture.Publisher();
        var second = fixture.Publisher();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? first : second).PublishAsync(fixture.Intent with { Priority = i + 1 }, default).AsTask()));
        Assert.Single(await fixture.Execution.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
        Assert.Single(await fixture.Execution.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()));
        var stored = await fixture.Execution.GetJobDetail(QuartzPublisher.JobKey(fixture.Intent));
        Assert.True(stored!.RequestsRecovery);
        Assert.All(stored.JobDataMap, pair => Assert.IsType<string>(pair.Value));
        var trigger = await fixture.Execution.GetTrigger(QuartzPublisher.TriggerKey(fixture.Intent));
        Assert.Equal(SimpleTriggerMisfireInstruction.FireNow, ((ISimpleTrigger)trigger!).MisfireInstruction);
        Assert.Equal(RetryPolicy.None, trigger!.RetryPolicy);
    }

    [Fact]
    public async Task Mismatched_stored_digest_is_visible_and_never_acknowledged()
    {
        await using var fixture = await Fixture.Create();
        var intent = fixture.Intent;
        var job = JobBuilder.Create<PublicationProbe>().WithIdentity(QuartzPublisher.JobKey(intent)).StoreDurably()
            .UsingJobData("PayloadDigest", new string('0', 64)).Build();
        await fixture.Execution.AddJob(job);
        Assert.Equal(0, await fixture.Reconcile());
        Assert.Equal("ProjectionIntegrityError", fixture.Store.Code);
        Assert.False(fixture.Store.Acknowledged);
    }

    [Fact]
    public async Task Forged_input_digest_is_rejected_before_writing()
    {
        await using var fixture = await Fixture.Create();
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await fixture.Publisher().PublishAsync(fixture.Intent with { PayloadDigest = "forged" }, default));
        Assert.Equal("ProjectionIntegrityError", error.Code);
        Assert.Empty(await fixture.Execution.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
    }

    [Theory]
    [InlineData("Unknown", 1)]
    [InlineData("ScheduleProjection", 1)]
    [InlineData("AlertDispatch", 1)]
    [InlineData("ExecutionDispatch", 2)]
    public async Task Unsupported_envelope_remains_visibly_failed(string kind, int version)
    {
        await using var fixture = await Fixture.Create();
        fixture.Store.Envelope = fixture.Store.Envelope with { Kind = kind, SchemaVersion = version };
        Assert.Equal(0, await fixture.Reconcile());
        Assert.False(fixture.Store.Acknowledged);
        Assert.Equal("UnsupportedProjection", fixture.Store.Code);
    }

    [Fact]
    public async Task Stale_revision_and_tampered_envelope_fail_closed()
    {
        await using var fixture = await Fixture.Create();
        fixture.Store.Revision = 2;
        await fixture.Reconcile();
        Assert.Equal("ProjectionIntegrityError", fixture.Store.Code);
        Assert.False(fixture.Store.Acknowledged);
        Assert.Empty(await fixture.Execution.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
    }

    [Fact]
    public async Task Permanently_errored_trigger_remains_visible_after_acknowledgement()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Reconcile();
        fixture.Fault = "ErrorTrigger";
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        Assert.Equal(0, await fixture.Reconcile());
        Assert.Equal("ProjectionTriggerError", fixture.Store.Code);
    }

    [Fact]
    public async Task Missing_published_trigger_is_repaired_only_for_eligible_business_state()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Reconcile();
        await fixture.Execution.UnscheduleJob(QuartzPublisher.TriggerKey(fixture.Intent));
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        Assert.Equal(1, await fixture.Reconcile());
        Assert.NotNull(await fixture.Execution.GetTrigger(QuartzPublisher.TriggerKey(fixture.Intent)));
    }

    [Fact]
    public async Task Retry_due_and_projection_failure_deadlines_are_preserved()
    {
        await using var fixture = await Fixture.Create();
        fixture.Store.Due = fixture.Clock.Now.AddHours(1);
        fixture.Store.Ordinal = 2;
        fixture.Store.State = "RetryWaiting";
        var original = fixture.Store.Envelope;
        fixture.Store.Envelope = original with
        {
            PayloadDigest = ProjectionEnvelope.ExecutionDigest(original.EntityId, 1, 2),
            Payload = JsonSerializer.SerializeToElement(new { executionId = original.EntityId, jobRevision = 1, attemptOrdinal = 2 })
        };
        fixture.Fault = "BeforeWrite";
        Assert.Equal(0, await fixture.Reconcile());
        Assert.False(fixture.Store.Acknowledged);
        fixture.Fault = null;
        Assert.Equal(0, await fixture.Reconcile());
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        await fixture.Reconcile();
        var trigger = await fixture.Execution.GetTrigger(QuartzPublisher.TriggerKey(fixture.Intent));
        Assert.True(trigger!.StartTimeUtc >= fixture.Store.Due);
    }

    [Fact]
    public async Task Published_identity_is_checked_even_if_trigger_is_missing()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Publisher().PublishAsync(fixture.Intent, default);
        await fixture.Execution.UnscheduleJob(QuartzPublisher.TriggerKey(fixture.Intent));
        var changed = fixture.Intent with { DueUtc = fixture.Intent.DueUtc.AddMinutes(1) };
        changed = changed with { PayloadDigest = changed.ComputeDigest() };
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await fixture.Publisher().PublishAsync(changed, default));
        Assert.Equal("ProjectionIntegrityError", error.Code);
    }

    [Fact]
    public async Task Unloaded_wrapper_identity_can_be_published_without_starting_execution()
    {
        await using var fixture = await Fixture.Create();
        await fixture.NamedPublisher().PublishAsync(fixture.Intent, default);
        await fixture.NamedPublisher().PublishAsync(fixture.Intent, default);
        var stored = await fixture.Execution.GetJobDetail(QuartzPublisher.JobKey(fixture.Intent));
        Assert.Equal("WorkerDock.Quartz.Dispatch.ExecuteTemplateJob, WorkerDock.Quartz", stored!.JobType.FullName);
        Assert.Equal(SchedulerStatus.Created, fixture.Execution.Status);
    }

    [Fact]
    public async Task Control_maintenance_bootstrap_is_idempotent_without_outbox_wakeup()
    {
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var schedule = await topology.CreateScheduleAsync(new(id, "bootstrap", 1), default);
        await ProjectionJob.RegisterAsync(schedule, default);
        await ProjectionJob.RegisterAsync(schedule, default);
        Assert.Equal(2, (await schedule.GetJobKeys(GroupMatcher<JobKey>.AnyGroup())).Count);
        Assert.Equal(2, (await schedule.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup())).Count);
        Assert.Equal(SchedulerStatus.Created, schedule.Status);
    }
    [Fact]
    public async Task Publisher_facade_still_denies_administration_and_secret_payloads()
    {
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var handle = await topology.CreateExecutionPublisherAsync(new(id, "facade", 1), default);
        await Assert.ThrowsAsync<WorkerDockException>(async () => await handle.Start());
        await Assert.ThrowsAsync<WorkerDockException>(async () => await handle.Standby());
        await Assert.ThrowsAsync<WorkerDockException>(async () => await handle.Clear());
        await Assert.ThrowsAsync<WorkerDockException>(async () => await handle.DeleteJob(new("forbidden")));
        await Assert.ThrowsAsync<WorkerDockException>(async () => await handle.UnscheduleJob(new("forbidden")));
        var job = JobBuilder.Create<PublicationProbe>().WithIdentity("forbidden").UsingJobData("Secret", "sentinel").Build();
        await Assert.ThrowsAsync<WorkerDockException>(async () => await handle.ScheduleJob(job, TriggerBuilder.Create().StartNow().Build()));
    }

    [Fact]
    public async Task Consumed_probe_and_restarted_reconciler_do_not_run_completed_delivery_again()
    {
        await using var fixture = await Fixture.Create();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = fixture.Intent.ExecutionId.ToString("N");
        PublicationProbe.Callbacks[key] = () =>
        {
            fixture.Store.State = "Succeeded";
            fixture.Store.AttemptExists = true;
            completion.TrySetResult();
        };
        fixture.Fault = "BeforeAck";
        await fixture.Reconcile();
        await fixture.Execution.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await fixture.Execution.GetTrigger(QuartzPublisher.TriggerKey(fixture.Intent), timeout.Token) is not null)
            await Task.Delay(10, timeout.Token);
        fixture.Fault = null;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        await fixture.Reconcile();
        Assert.Null(await fixture.Execution.GetTrigger(QuartzPublisher.TriggerKey(fixture.Intent)));
        Assert.Equal(1, PublicationProbe.Entries.TryRemove(key, out var entries) ? entries : 0);
        await fixture.Execution.Shutdown(true);
    }

    [Fact]
    public async Task Started_control_scheduler_projects_without_starting_execution_publisher()
    {
        await using var fixture = await Fixture.Create();
        var id = Guid.NewGuid();
        await using var topology = QuartzTopology.CreateMemory(id, "Development");
        var job = new ProjectionJob(new(fixture.Store, fixture.Store, fixture.Publisher(), fixture.Clock));
        var schedule = await topology.CreateScheduleAsync(new(id, "control", 1), new FixedJobFactory(job), default);
        await ProjectionJob.RegisterAsync(schedule, default);
        await schedule.Start();
        await fixture.Store.Ack.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await schedule.Shutdown(true);
        Assert.Equal(SchedulerStatus.Created, fixture.Execution.Status);
        Assert.Single(await fixture.Execution.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup()));
    }

    private sealed class FixedJobFactory(IJob job) : IJobFactory
    {
        public ValueTask<JobScope> CreateJob(TriggerFiredBundle bundle, IScheduler scheduler, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new JobScope(job));
        public ValueTask ReturnJob(JobScope scope, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Existing_trigger_cannot_bypass_future_retry_deadline_or_template_group()
    {
        await using var fixture = await Fixture.Create();
        fixture.Store.Due = fixture.Clock.Now.AddHours(1);
        await fixture.Publisher().PublishAsync(fixture.Intent, default);
        var trigger = TriggerBuilder.Create().WithIdentity(QuartzPublisher.TriggerKey(fixture.Intent))
            .ForJob(QuartzPublisher.JobKey(fixture.Intent)).WithExecutionGroup("wrong-template").StartNow()
            .WithRetryPolicy(RetryPolicy.None).WithSimpleSchedule(x => x.WithRepeatCount(0)
                .WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
        await fixture.Execution.RescheduleJob(trigger.Key, trigger);
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await fixture.Publisher().PublishAsync(fixture.Intent, default));
        Assert.Equal("ProjectionIntegrityError", error.Code);
    }

    public sealed class PublicationProbe : IJob
    {
        public static readonly ConcurrentDictionary<string, Action> Callbacks = new();
        public static readonly ConcurrentDictionary<string, int> Entries = new();
        public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        {
            var key = context.MergedJobDataMap.GetString("ExecutionId")!;
            Entries.AddOrUpdate(key, 1, (_, value) => value + 1);
            if (Callbacks.TryRemove(key, out var callback)) callback();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store(Clock clock) : IProjectionStore, IDispatchStateStore, IProjectionReviewStore
    {
        public ProjectionEnvelope Envelope = CreateEnvelope(clock.Now);
        public bool Acknowledged;
        public readonly TaskCompletionSource Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Ordinal = 1;
        public bool Retired;
        public bool AttemptExists;
        public string State = "Queued";
        public long Revision = 1;
        public string? Code;
        public string? Fault;
        public DateTimeOffset Next;
        public DateTimeOffset Due = clock.Now;
        public DispatchIntent Intent
        {
            get
            {
                var intent = new DispatchIntent(Envelope.IntentId, Envelope.EntityId, Ordinal, "template-probe", Due, 5, Revision, "");
                return intent with { PayloadDigest = intent.ComputeDigest() };
            }
        }
        public ValueTask<DispatchState> ReadAsync(ProjectionEnvelope envelope, int attemptOrdinal, CancellationToken ct) =>
            ValueTask.FromResult(new DispatchState(State, AttemptExists, Intent));
        public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPendingAsync(int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ProjectionEnvelope>>(!Acknowledged && Next <= clock.Now ? [Envelope] : []);
        public ValueTask<IReadOnlyList<ProjectionEnvelope>> ReadPublishedAsync(int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ProjectionEnvelope>>(Acknowledged && !Retired && Next <= clock.Now ? [Envelope] : []);
        public ValueTask CompleteReviewAsync(Guid intentId, bool retired, DateTimeOffset reviewAtUtc, CancellationToken ct)
        {
            Retired = retired; Next = reviewAtUtc; return ValueTask.CompletedTask;
        }
        public ValueTask AcknowledgeAsync(Guid intentId, string payloadDigest, CancellationToken ct)
        {
            Assert.Equal(Envelope.IntentId, intentId);
            Assert.Equal(Envelope.PayloadDigest, payloadDigest);
            if (Fault == "BeforeAck") throw new WorkerDockException("PersistenceUnavailable");
            Acknowledged = true;
            Ack.TrySetResult();
            if (Fault == "AfterAck") throw new WorkerDockException("PersistenceUnavailable");
            return ValueTask.CompletedTask;
        }
        public ValueTask RecordFailureAsync(Guid intentId, string safeCode, DateTimeOffset retryAtUtc, CancellationToken ct)
        {
            Code = safeCode; Next = retryAtUtc; return ValueTask.CompletedTask;
        }
        private static ProjectionEnvelope CreateEnvelope(DateTimeOffset now)
        {
            var executionId = Guid.NewGuid();
            return new(Guid.NewGuid(), "ExecutionDispatch", 1, executionId, 1,
                ProjectionEnvelope.ExecutionDigest(executionId, 1), now,
                JsonSerializer.SerializeToElement(new { executionId, jobRevision = 1 }));
        }
    }

    private sealed class Fixture(QuartzTopology topology, IScheduler execution, IScheduler handle) : IAsyncDisposable
    {
        public readonly Clock Clock = new();
        public Store Store = null!;
        public IScheduler Execution => execution;
        public DispatchIntent Intent => Store.Intent;
        public string? Fault { set { Store.Fault = value; ((FaultScheduler)Handle).Fault = value; } }
        private IScheduler Handle = handle;
        public QuartzPublisher Publisher() => new(Handle, typeof(PublicationProbe));
        public QuartzPublisher NamedPublisher() => new(Handle, (JobType)"WorkerDock.Quartz.Dispatch.ExecuteTemplateJob, WorkerDock.Quartz");
        public ValueTask<int> Reconcile() => new DispatchReconciler(Store, Store, Publisher(), Clock).ReconcileAsync(20, default);
        public static async Task<Fixture> Create()
        {
            var id = Guid.NewGuid();
            var topology = QuartzTopology.CreateMemory(id, "Development");
            var node = new QuartzNodeOptions(id, "publication", 1);
            var execution = await topology.CreateExecutionAsync(node, default);
            var facade = await topology.CreateExecutionPublisherAsync(node, default);
            var proxy = DispatchProxy.Create<IScheduler, FaultScheduler>();
            ((FaultScheduler)proxy).Inner = facade;
            var fixture = new Fixture(topology, execution, proxy);
            fixture.Store = new(fixture.Clock);
            return fixture;
        }
        public ValueTask DisposeAsync() => topology.DisposeAsync();
    }

    public class FaultScheduler : DispatchProxy
    {
        public IScheduler Inner = null!;
        public string? Fault;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "GetTriggerState" && Fault == "ErrorTrigger") return ValueTask.FromResult(TriggerState.Error);
            if (targetMethod.Name == "ScheduleJob" && Fault == "BeforeWrite") throw new SchedulerException("synthetic");
            if (targetMethod.Name == "ScheduleJob" && Fault == "AfterWrite") return WriteThenFail(args!);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async ValueTask<DateTimeOffset> WriteThenFail(object?[] args)
        {
            await Inner.ScheduleJob((IJobDetail)args[0]!, (ITrigger)args[1]!, (ScheduleJobOptions)args[2]!, (CancellationToken)args[3]!);
            throw new SchedulerException("synthetic");
        }
    }
}
