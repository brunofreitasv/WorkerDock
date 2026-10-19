using WorkerDock.Infrastructure.Logging;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Application.Observability;

namespace WorkerDock.UnitTests;

public sealed class TelemetryProjectionTests
{
    [Fact]
    public async Task Failing_optional_log_sink_does_not_escape_into_execution()
    {
        var log = new SafeExecutionLog(_ => throw new IOException("synthetic exporter failure"));
        var error = await Record.ExceptionAsync(async () => await log.WriteAsync(
            "TemplateCompleted",
            new Dictionary<string, string> { ["outcome"] = "Success", ["durationMs"] = "4" },
            default));
        Assert.Null(error);
    }

    [Fact]
    public async Task Flooded_and_invalid_diagnostics_are_dropped_with_a_bounded_marker()
    {
        var emitted = new List<string>();
        var log = new SafeExecutionLog(emitted.Add);
        for (var i = 0; i < 1000; i++)
            await log.WriteAsync("TemplateCompleted", new Dictionary<string, string> { ["outcome"] = "Success", ["durationMs"] = i.ToString() }, default);
        var invalid = await Record.ExceptionAsync(async () => await log.WriteAsync(
            "TemplateFailed", new Dictionary<string, string> { ["message"] = "SAFE_SENTINEL_SHOULD_NOT_ESCAPE" }, default));

        Assert.Null(invalid);
        Assert.True(emitted.Count <= 257);
        Assert.Contains("DiagnosticsDropped", string.Join("\n", emitted));
        Assert.DoesNotContain("SAFE_SENTINEL_SHOULD_NOT_ESCAPE", string.Join("\n", emitted));
    }

    [Fact]
    public void Metric_tags_are_low_cardinality_and_exclude_identifiers()
    {
        var tags = MetricTagProjection.Create("workerdock.sql-server", "Succeeded", "worker");

        Assert.Equal(new[] { "template_id", "outcome", "component" }, tags.Keys);
        Assert.DoesNotContain("execution_id", tags.Keys);
        Assert.DoesNotContain("correlation_id", tags.Keys);
        Assert.Equal("other", MetricTagProjection.Create("tenant-specific-template", "Succeeded", "worker")["template_id"]);
    }

    [Fact]
    public void Audit_projection_does_not_expose_details_payload()
    {
        var projected = AuditEventProjection.Project(new AuditEventRecord(Guid.NewGuid(), "JobChanged", "operator", "OIDC",
            Guid.NewGuid(), DateTimeOffset.UtcNow, "SECRET_SENTINEL"));

        Assert.DoesNotContain("Details", projected.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("SECRET_SENTINEL", System.Text.Json.JsonSerializer.Serialize(projected));
    }

    [Fact]
    public async Task Slow_event_sink_is_bounded_and_does_not_block_the_caller()
    {
        var sink = new HoldingEventSink();
        await using var buffer = new BufferedExecutionEvents(sink, capacity: 1);
        var entry = new ExecutionEvent(Guid.NewGuid(), Guid.NewGuid(), "Supervisor", 1, DateTimeOffset.UtcNow, "TemplateCompleted", new Dictionary<string, string>());

        await buffer.AppendAsync([entry], default);
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await buffer.AppendAsync([entry with { Sequence = 2 }], default);
        for (var i = 3; i < 103; i++) await buffer.AppendAsync([entry with { Sequence = i }], default);

        Assert.True(buffer.DroppedCount > 0);
        sink.Release.TrySetResult();
    }

    private sealed class HoldingEventSink : IExecutionEvents
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask AppendAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }
}
