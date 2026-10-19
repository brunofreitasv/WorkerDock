using System.Globalization;
using global::Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;

namespace WorkerDock.Quartz.Scheduling;

[DisallowConcurrentExecution]
[RetryPolicy(0)]
public sealed class OccurrenceDispatchJob(IOccurrenceWriter writer, TimeProvider clock) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var data = context.JobDetail.JobDataMap;
        if (data.Count != 2 || data.Any(pair => pair.Value is not string) ||
            !Guid.TryParseExact(data.GetString("ScheduleId"), "N", out var scheduleId) ||
            !long.TryParse(data.GetString("ScheduleRevision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
            throw new WorkerDockException("ProjectionIntegrityError");
        return MaterializeAsync(scheduleId, revision, context, cancellationToken);
    }

    private async ValueTask MaterializeAsync(Guid scheduleId, long revision, IJobExecutionContext context, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        await writer.MaterializeAsync(scheduleId, revision, clock.GetUtcNow().ToUniversalTime(), bounded.Token);
    }
}
