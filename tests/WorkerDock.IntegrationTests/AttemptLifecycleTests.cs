using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Jobs;

namespace WorkerDock.IntegrationTests;

public sealed class AttemptLifecycleTests
{
    private static readonly ActorContext Actor = new("operator", "Anonymous", Guid.NewGuid());
    internal static ExecutionPolicy Policy(bool replay = false, int attempts = 2, TimeSpan? timeout = null) =>
        new(timeout, attempts, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), .2, ["Busy"], replay, "Queue", 100, 5);
    internal static ExecutionDescriptor Descriptor() => new(Guid.NewGuid(), 1, "test", "1", Guid.NewGuid().ToString("N"),
        JsonSerializer.SerializeToElement(new { }), null, null, JsonSerializer.SerializeToElement(new { }), null);
    private static TemplateResult Result(string outcome = "Success", string code = "Ok", bool effects = false, TimeSpan? hint = null) =>
        new(outcome, code, effects, JsonSerializer.SerializeToElement(new { }), hint);

    [Fact]
    public async Task Memory_job_commands_and_execution_controls_share_the_same_execution_and_receipt()
    {
        var revision = new JobRevision(Guid.NewGuid(), 1, "test", "workerdock.http", "1.0.0", JsonSerializer.SerializeToElement(new { }), null, Policy());
        var commands = new InMemoryJobCommands(new Clock(), [revision], templateCatalog: JobCommandTestCatalog.Create());
        var run = await commands.RunNowAsync(revision.JobId, "memory-control", Actor, default);
        var controls = Assert.IsAssignableFrom<IExecutionControls>(commands);
        var stop = await controls.StopAsync(run.TargetId, false, "queued stop", Actor, default);
        Assert.Equal("Stopped", commands.GetExecution(run.TargetId)!.State);
        Assert.Equal(stop, await commands.GetCommandAsync(stop.CommandId, default));
        Assert.Equal(3, commands.AuditCount);
        Assert.Equal("Failed", (await commands.GetCommandAsync(run.CommandId, default)).State);
        Assert.Equal("Failed", Assert.Single(commands.AuditEvents, row => row.EventType == "CommandSettled").Outcome);
    }

    [Fact]
    public async Task Concurrent_delivery_and_lost_start_ack_authorize_only_one_owner()
    {
        var store = new InMemoryAttemptStore(new Clock(), Descriptor(), Policy());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AttemptStart> Start(string boot) { await ready.Task; return await store.BeginAsync(store.Intent, boot, default); }
        var first = Start("a"); var second = Start("b"); ready.SetResult();
        var starts = await Task.WhenAll(first, second);
        Assert.Single(starts, x => x.Decision == "Started");
        Assert.Single(starts, x => x.Decision == "AlreadyOwned");
        Assert.Equal("AlreadyOwned", (await store.BeginAsync(store.Intent, "a", default)).Decision);
        Assert.Equal(1, store.Snapshot.Ordinal);
    }

    [Fact]
    public async Task Unknown_effects_hold_unless_explicitly_replay_safe()
    {
        foreach (var replay in new[] { false, true })
        {
            var clock = new Clock(); var store = new InMemoryAttemptStore(clock, Descriptor(), Policy(replay));
            var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
            clock.Now += TimeSpan.FromSeconds(60);
            Assert.Null(await store.RenewAsync(lease, default));
            Assert.Equal(1, await store.ReviewExpiredAsync(10, default));
            Assert.Equal(replay ? "RetryWaiting" : "NeedsReview", store.Snapshot.State);
            Assert.True(store.Snapshot.PermitRetained);
            Assert.Equal("Completed", (await store.BeginAsync(store.Intent, "other", default)).Decision);
            await store.CompleteAsync(lease, new(Result(), "Completed", true, false), default);
            Assert.Equal(replay ? "RetryWaiting" : "NeedsReview", store.Snapshot.State);
        }
    }

    [Fact]
    public async Task Stop_first_wins_and_completion_first_stays_successful()
    {
        foreach (var stopFirst in new[] { true, false })
        {
            var store = new InMemoryAttemptStore(new Clock(), Descriptor(), Policy(true));
            var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
            if (stopFirst) await store.StopAsync(lease.ExecutionId, false, "operator stop", Actor, default);
            await store.CompleteAsync(lease, new(Result(), "Completed", true, false), default);
            if (!stopFirst) await store.StopAsync(lease.ExecutionId, false, "late stop", Actor, default);
            Assert.Equal(stopFirst ? "Stopped" : "Succeeded", store.Snapshot.State);
            Assert.Equal(stopFirst ? "Stopped" : "Succeeded", store.Snapshot.AttemptState);
            Assert.Empty(store.Retries);
            Assert.False(store.Snapshot.PermitRetained);
        }
    }

    [Fact]
    public async Task Confirmed_stop_retains_unknown_effect_certainty_without_an_automatic_retry()
    {
        var store = new InMemoryAttemptStore(new Clock(), Descriptor(), Policy(true));
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        await store.StopAsync(lease.ExecutionId, true, "confirmed force", Actor, default);
        await store.CompleteAsync(lease, new(null, "Stopped", true, true), default);
        Assert.Equal("Stopped", store.Snapshot.State);
        Assert.True(store.Snapshot.EffectsMayHaveOccurred);
        Assert.Empty(store.Retries);
    }

    [Fact]
    public async Task Uncertain_completion_and_repeated_ack_never_turn_into_success()
    {
        var store = new InMemoryAttemptStore(new Clock(), Descriptor(), Policy());
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        await store.CompleteAsync(lease, new(null, "ProcessFailed", true, true), default);
        Assert.Equal("NeedsReview", store.Snapshot.State);
        await store.CompleteAsync(lease, new(Result(), "Completed", true, false), default);
        Assert.Equal("NeedsReview", store.Snapshot.State);
    }

    [Fact]
    public async Task Stop_without_confirmed_exit_remains_reviewable_and_never_retries()
    {
        var clock = new Clock(); var store = new InMemoryAttemptStore(clock, Descriptor(), Policy(true));
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        await store.StopAsync(lease.ExecutionId, true, "confirmed force", Actor, default);
        clock.Now += TimeSpan.FromMinutes(1);
        await store.ReviewExpiredAsync(10, default);
        Assert.Equal("NeedsReview", store.Snapshot.State);
        Assert.True(store.Snapshot.StopRequested);
        Assert.True(store.Snapshot.PermitRetained);
        Assert.Empty(store.Retries);
    }

    [Fact]
    public async Task Retry_is_bounded_preserves_key_and_obeys_due_time_and_budget()
    {
        var clock = new Clock(); var descriptor = Descriptor(); var policy = Policy();
        var store = new InMemoryAttemptStore(clock, descriptor, policy);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        Assert.Null(lease.DeadlineUtc);
        await store.CompleteAsync(lease, new(Result("TransientFailure", "Busy", false, TimeSpan.FromDays(1)), "Completed", true, false), default);
        Assert.Equal("RetryWaiting", store.Snapshot.State);
        var retry = Assert.Single(store.Retries);
        Assert.InRange(retry.DueUtc - clock.Now, TimeSpan.FromSeconds(2), policy.RetryMaxDelay);
        Assert.Equal("Held", (await store.BeginAsync(retry, "boot", default)).Decision);
        clock.Now = retry.DueUtc;
        var next = (await store.BeginAsync(retry, "boot", default)).Lease!;
        Assert.Equal(descriptor.IdempotencyKey, (await store.GetDescriptorAsync(next, default)).IdempotencyKey);
        await store.CompleteAsync(next, new(Result("TransientFailure", "Busy"), "Completed", true, false), default);
        Assert.Equal("Failed", store.Snapshot.State);
        Assert.Single(store.Retries);
    }

    [Theory]
    [InlineData("PermanentFailure", "Busy", "Failed")]
    [InlineData("TransientFailure", "Unlisted", "Failed")]
    public async Task Failure_policy_does_not_grant_retry_from_a_hint(string outcome, string code, string expected)
    {
        var store = new InMemoryAttemptStore(new Clock(), Descriptor(), Policy());
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        await store.CompleteAsync(lease, new(Result(outcome, code, false, TimeSpan.FromSeconds(5)), "Completed", true, false), default);
        Assert.Equal(expected, store.Snapshot.State); Assert.Empty(store.Retries);
    }

    [Fact]
    public async Task Timeout_without_handler_result_is_timed_out_and_deadline_starts_at_admission()
    {
        var clock = new Clock(); var store = new InMemoryAttemptStore(clock, Descriptor(), Policy(timeout: TimeSpan.FromSeconds(3)));
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        Assert.Equal(clock.Now.AddSeconds(3), lease.DeadlineUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), OwnershipDefaults.CancellationGrace);
        await store.CompleteAsync(lease, new(null, "TimedOut", true, false), default);
        Assert.Equal("TimedOut", store.Snapshot.State);
    }

    [Fact]
    public async Task Review_resolution_is_audited_and_retains_original_execution_key()
    {
        var clock = new Clock(); var descriptor = Descriptor(); var store = new InMemoryAttemptStore(clock, descriptor, Policy());
        await store.BeginAsync(store.Intent, "crashed-before-launch", default);
        clock.Now += TimeSpan.FromMinutes(1); await store.ReviewExpiredAsync(10, default);
        await store.ResolveAsync(descriptor.ExecutionId, "AuthorizeRetry", "target reconciliation permits replay", Actor, default);
        var retry = Assert.Single(store.Retries); clock.Now = retry.DueUtc;
        var lease = (await store.BeginAsync(retry, "new", default)).Lease!;
        Assert.Equal(descriptor.IdempotencyKey, (await store.GetDescriptorAsync(lease, default)).IdempotencyKey);
        Assert.Single(store.Audits);
        Assert.Equal("AttemptOwnershipLost", (await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await store.GetDescriptorAsync(lease with { Generation = lease.Generation - 1 }, default))).Code);
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
