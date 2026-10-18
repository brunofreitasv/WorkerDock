namespace WorkerDock.Application.Workers;

public interface ICapacityStore
{
    ValueTask<T> TransactAsync<T>(Func<CapacityState, DateTimeOffset, T> change, CancellationToken ct);
}
