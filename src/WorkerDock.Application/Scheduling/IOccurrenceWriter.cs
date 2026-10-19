namespace WorkerDock.Application.Scheduling;

public interface IOccurrenceWriter
{
    ValueTask<int> MaterializeAsync(Guid scheduleId, long scheduleRevision, DateTimeOffset throughUtc, CancellationToken ct);
}
