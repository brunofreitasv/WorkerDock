namespace WorkerDock.Application.Alerts;

public interface IAlertEvaluator
{
    ValueTask<int> EvaluateAsync(DateTimeOffset throughUtc, int limit, CancellationToken ct);
}
