namespace WorkerDock.Application.Dispatch;
/// <summary>Bounded business admission maintenance, performed after publication has made progress.</summary>
public interface IProjectionMaintenanceStore
{
    ValueTask MaintainAsync(CancellationToken ct);
}
