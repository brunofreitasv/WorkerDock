using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.IntegrationTests;

public sealed class ExecutionValueSnapshotTests
{
    private static readonly DynamicValuePlan Plan = new(1, "UTC", [
        new("/parameters/0/value", DynamicValueSource.ReferenceDate, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate),
        new("/parameters/1/value", DynamicValueSource.FirstExecutionStart, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoUtcTimestamp)]);

    private static ExecutionDescriptor Descriptor(ExecutionValueContext context) => new(Guid.NewGuid(), 1, "test", "1", "key",
        JsonSerializer.SerializeToElement(new { parameters = new[] { new { value = "old" }, new { value = "old" } } }),
        null, null, JsonSerializer.SerializeToElement(new { }), null, context);

    [Fact]
    public async Task Retry_after_midnight_reuses_snapshot()
    {
        var clock = new AttemptLifecycleTests.Clock { Now = new(2026, 10, 20, 23, 59, 0, TimeSpan.Zero) };
        var context = new ExecutionValueContext(1, "UTC", "Manual", null, clock.Now, null, null);
        var store = new InMemoryAttemptStore(clock, Descriptor(context), AttemptLifecycleTests.Policy(), dynamicValues: Plan);
        var firstLease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var first = await store.GetDescriptorAsync(firstLease, default);
        await store.CompleteAsync(firstLease, new(new("TransientFailure", "Busy", false, JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);
        var retry = Assert.Single(store.Retries);
        clock.Now = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);
        var secondLease = (await store.BeginAsync(retry, "boot", default)).Lease!;
        var second = await store.GetDescriptorAsync(secondLease, default);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first.ValueSnapshot), JsonSerializer.SerializeToElement(second.ValueSnapshot)));
        Assert.True(JsonElement.DeepEquals(first.Configuration, second.Configuration));
        Assert.Equal("2026-10-20", first.Configuration.GetProperty("parameters")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Resolution_failure_is_permanent_before_handler()
    {
        var clock = new AttemptLifecycleTests.Clock();
        var context = new ExecutionValueContext(1, "UTC", "Manual", null, clock.Now, null, null);
        var plan = new DynamicValuePlan(1, "UTC", [new("/parameters/0/value", DynamicValueSource.ScheduledTimestamp, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoUtcTimestamp)]);
        var targetCallCount = 0;
        var store = new InMemoryAttemptStore(clock, Descriptor(context), AttemptLifecycleTests.Policy(),
            integrationSnapshot: _ => { targetCallCount++; return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { })); },
            dynamicValues: plan);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var descriptor = await store.GetDescriptorAsync(lease, default);
        Assert.Null(descriptor.ValueSnapshot);
        Assert.Equal("DynamicSourceUnavailable", descriptor.ValueResolutionError);
        Assert.Equal(0, targetCallCount);
    }

    [Fact]
    public async Task Changed_integration_settings_do_not_overwrite_snapshot()
    {
        var clock = new AttemptLifecycleTests.Clock();
        var revision = 0;
        var store = new InMemoryAttemptStore(clock, Descriptor(new(1, "UTC", "Manual", null, clock.Now, null, null)),
            AttemptLifecycleTests.Policy(),
            integrationSnapshot: _ => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { version = ++revision })),
            dynamicValues: Plan);
        var firstLease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var first = await store.GetDescriptorAsync(firstLease, default);
        await store.CompleteAsync(firstLease, new(new("TransientFailure", "Busy", false, JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);
        var retry = Assert.Single(store.Retries);
        clock.Now = retry.DueUtc;
        var nextLease = (await store.BeginAsync(retry, "boot", default)).Lease!;
        var next = await store.GetDescriptorAsync(nextLease, default);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first.ValueSnapshot), JsonSerializer.SerializeToElement(next.ValueSnapshot)));
        Assert.Equal(1, first.IntegrationConfiguration.GetProperty("version").GetInt32());
        Assert.Equal(2, next.IntegrationConfiguration.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Concurrent_preparation_commits_one_snapshot()
    {
        var clock = new AttemptLifecycleTests.Clock();
        var store = new InMemoryAttemptStore(clock, Descriptor(new(1, "UTC", "Manual", null, clock.Now, null, null)),
            AttemptLifecycleTests.Policy(), dynamicValues: Plan);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var descriptors = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.GetDescriptorAsync(lease, default).AsTask()));
        var first = JsonSerializer.SerializeToElement(descriptors[0].ValueSnapshot);
        Assert.All(descriptors, item => Assert.True(JsonElement.DeepEquals(first, JsonSerializer.SerializeToElement(item.ValueSnapshot))));
        Assert.Equal(clock.Now, descriptors[0].ValueContext!.FirstStartedAtUtc);
    }

    [Fact]
    public async Task Dynamic_plan_without_context_fails_before_execution()
    {
        var store = new InMemoryAttemptStore(new AttemptLifecycleTests.Clock(), AttemptLifecycleTests.Descriptor(),
            AttemptLifecycleTests.Policy(), dynamicValues: Plan);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var descriptor = await store.GetDescriptorAsync(lease, default);
        Assert.Equal("DynamicSourceUnavailable", descriptor.ValueResolutionError);
        Assert.Null(descriptor.ValueSnapshot);
    }

    [Fact]
    public async Task Rejected_start_does_not_assign_first_start()
    {
        var clock = new AttemptLifecycleTests.Clock();
        var store = new InMemoryAttemptStore(clock, Descriptor(new(1, "UTC", "Manual", null, clock.Now, null, null)),
            AttemptLifecycleTests.Policy(), dynamicValues: Plan);
        clock.Now = clock.Now.AddMinutes(-1);
        Assert.Equal("Held", (await store.BeginAsync(store.Intent, "boot", default)).Decision);
        Assert.Null(store.AcceptedDescriptor.ValueContext!.FirstStartedAtUtc);
        clock.Now = clock.Now.AddMinutes(1);
        var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        Assert.Equal(clock.Now, (await store.GetDescriptorAsync(lease, default)).ValueContext!.FirstStartedAtUtc);
    }

    [Fact]
    public void Actual_snapshot_over_16384_bytes_is_rejected()
    {
        var bindings = Enumerable.Range(0, 128).Select(index =>
            new DynamicValueBinding("/" + new string('p', 100) + index, DynamicValueSource.IdempotencyKey,
                null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.String)).ToArray();
        var configuration = JsonSerializer.SerializeToElement(bindings.ToDictionary(binding => binding.Target[1..], _ => "old"));
        var context = new ExecutionValueContext(1, "UTC", "Manual", null, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);
        var descriptor = new ExecutionDescriptor(Guid.NewGuid(), 1, "test", "1", new string('x', 100), configuration,
            null, null, JsonSerializer.SerializeToElement(new { }), null, context);
        var result = new ExecutionValuePreparation(new DynamicValueResolver()).Prepare(descriptor, new(1, "UTC", bindings));
        Assert.Equal("DynamicSnapshotTooLarge", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Complete_resolved_envelope_over_65536_bytes_is_rejected()
    {
        var configuration = JsonSerializer.SerializeToElement(new { value = "old", filler = new string('x', 30_000) });
        var descriptor = new ExecutionDescriptor(Guid.NewGuid(), 1, "test", "1", "key", configuration, null,
            JsonSerializer.SerializeToElement(new { filler = new string('y', 30_000) }), JsonSerializer.SerializeToElement(new { }), null,
            new(1, "UTC", "Manual", null, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow));
        var plan = new DynamicValuePlan(1, "UTC", [new("/value", DynamicValueSource.ReferenceDate, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate)]);
        var result = new ExecutionValuePreparation(new DynamicValueResolver()).Prepare(descriptor, plan);
        Assert.Equal("DynamicSnapshotTooLarge", result.ErrorCode);
        Assert.Null(result.Snapshot);
    }
}
