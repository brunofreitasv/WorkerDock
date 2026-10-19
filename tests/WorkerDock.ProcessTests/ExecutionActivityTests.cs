using System.Diagnostics;
using WorkerDock.Application.Observability;
using WorkerDock.Application.Executions;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Runtime.Ownership;

namespace WorkerDock.ProcessTests;

public sealed class ExecutionActivityTests
{
    [Fact]
    public async Task Completed_execution_emits_worker_dock_lifecycle_activity()
    {
        var path = Path.Combine(Path.GetTempPath(), "WorkerDockActivity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var previousSecret = Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SECRET");
        Environment.SetEnvironmentVariable("WORKERDOCK_TEST_SECRET", "activity-test-secret");
        var executionId = Guid.NewGuid();
        Guid attemptId = Guid.Empty;
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "WorkerDock",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped = activity
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            var descriptor = FixtureProgram.Descriptor("secret", path) with { ExecutionId = executionId };
            var policy = new ExecutionPolicy(null, 1, TimeSpan.Zero, TimeSpan.Zero, 0, [], false, "Queue", 100, 5);
            var store = new InMemoryAttemptStore(TimeProvider.System, descriptor, policy);
            var lease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
            attemptId = lease.AttemptId;
            var monitor = new OwnershipMonitor(store, store, FixtureProgram.Supervisor(), TimeProvider.System);
            await monitor.RunAsync(lease, await store.GetDescriptorAsync(lease, default), TimeProvider.System.GetTimestamp(), default);
            Assert.Equal("Succeeded", store.Snapshot.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WORKERDOCK_TEST_SECRET", previousSecret);
            Directory.Delete(path, true);
        }

        var activity = Assert.IsType<Activity>(stopped);
        Assert.Equal("workerdock.execution", activity.OperationName);
        Assert.Same(ExecutionActivitySource.Source, TelemetryRegistration.Activities);
        Assert.Equal(executionId.ToString("D"), activity.GetTagItem("execution.id"));
        Assert.Equal(attemptId.ToString("D"), activity.GetTagItem("attempt.id"));
        Assert.Equal("secret", activity.GetTagItem("template.id"));
        Assert.Equal("1", activity.GetTagItem("template.version"));
        Assert.Equal("Success", activity.GetTagItem("handler.outcome"));
        Assert.Equal("Succeeded", activity.GetTagItem("outcome"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.DoesNotContain("activity-test-secret", string.Join("|", activity.TagObjects.Select(tag => tag.Value?.ToString())));
    }
}
