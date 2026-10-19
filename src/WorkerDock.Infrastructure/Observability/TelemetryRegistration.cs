using System.Diagnostics;
using System.Diagnostics.Metrics;
using WorkerDock.Application.Observability;

namespace WorkerDock.Infrastructure.Observability;

/// <summary>OpenTelemetry-compatible instruments; exporters/listeners are attached only by the host.</summary>
public static class TelemetryRegistration
{
    public const string InstrumentationName = ExecutionActivitySource.InstrumentationName;
    public static readonly ActivitySource Activities = ExecutionActivitySource.Source;
    public static readonly Meter Meter = new(InstrumentationName);
    private static readonly Counter<long> Executions = Meter.CreateCounter<long>("workerdock.executions");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("workerdock.retries");
    private static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("workerdock.timeouts");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("workerdock.execution.duration", "ms");
    private static readonly Histogram<double> TriggerDelay = Meter.CreateHistogram<double>("workerdock.execution.trigger_delay", "ms");
    private static readonly Histogram<double> QueueDelay = Meter.CreateHistogram<double>("workerdock.execution.queue_delay", "ms");
    private static readonly Histogram<double> HeartbeatAge = Meter.CreateHistogram<double>("workerdock.worker.heartbeat_age", "ms");

    public static void RecordAggregate(string templateId, string outcome, long count, long retries, long timeouts,
        long durationMsTotal, long triggerDelayMsTotal, long queueDelayMsTotal)
    {
        var tags = MetricTagProjection.Create(templateId, outcome, "worker");
        var tagList = ToTagList(tags);
        if (count > 0) Executions.Add(count, tagList);
        if (retries > 0) Retries.Add(retries, tagList);
        if (timeouts > 0) Timeouts.Add(timeouts, tagList);
        if (count > 0)
        {
            Duration.Record((double)durationMsTotal / count, tagList);
            TriggerDelay.Record((double)triggerDelayMsTotal / count, tagList);
            QueueDelay.Record((double)queueDelayMsTotal / count, tagList);
        }
    }

    public static void RecordHeartbeatAge(TimeSpan age, string component = "worker") =>
        HeartbeatAge.Record(Math.Max(0, age.TotalMilliseconds), ToTagList(MetricTagProjection.Create(null, null, component)));

    private static TagList ToTagList(IReadOnlyDictionary<string, string> tags)
    {
        var result = new TagList();
        foreach (var (key, value) in tags) result.Add(key, value);
        return result;
    }
}
