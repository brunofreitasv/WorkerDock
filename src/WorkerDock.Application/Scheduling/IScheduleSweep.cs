namespace WorkerDock.Application.Scheduling;

public interface IScheduleSweep
{
    ValueTask<int> RunDueAsync(DateTimeOffset throughUtc, int maxSchedules, CancellationToken ct);
    ValueTask<int> RefreshZoneRulesAsync(int maxSchedules, CancellationToken ct);
}