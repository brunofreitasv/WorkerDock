namespace WorkerDock.Application.Scheduling;

public interface IScheduleProjectionMaintenance
{
    ValueTask<int> ReconcileZoneRuleChangesAsync(CancellationToken ct);
}
