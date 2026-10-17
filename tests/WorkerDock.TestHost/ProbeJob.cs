using System.Collections.Concurrent;
using Quartz;
namespace WorkerDock.TestHost;
public sealed class ProbeJob : IJob
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Releases = new();
    public static void Release(string id) => Releases.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var data = context.MergedJobDataMap;
        if (data.Keys.Except(new[] { "ExecutionId", "DeliveryId", "AttemptOrdinal" }).Any() || data.Values.Any(value => value is not string))
            throw new InvalidOperationException("InvalidProbeData");
        var id = data.GetString("DeliveryId")!;
        var release = Releases.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        Console.WriteLine("START " + id);
        await release.Task.WaitAsync(cancellationToken);
        Console.WriteLine("END " + id);
        Releases.TryRemove(id, out _);
    }
}
