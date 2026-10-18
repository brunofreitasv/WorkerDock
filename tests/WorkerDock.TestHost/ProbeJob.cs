using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
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
        await RecordAsync(id, 'S', cancellationToken);
        Console.WriteLine("START " + id);
        try { await release.Task.WaitAsync(cancellationToken); }
        finally
        {
            await RecordAsync(id, 'E', CancellationToken.None);
            Console.WriteLine("END " + id);
            Releases.TryRemove(id, out _);
        }
    }

    private static async Task RecordAsync(string delivery, char kind, CancellationToken ct)
    {
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_CONNECTION"));
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [workerdock].QuartzProbeState
            SET Active = Active + @Delta,
                MaximumActive = CASE WHEN Active + @Delta > MaximumActive THEN Active + @Delta ELSE MaximumActive END
            WHERE Id = 1;
            INSERT [workerdock].QuartzProbeEvents(DeliveryId, BootId, EventKind) VALUES (@Delivery, @Boot, @Kind);
            """;
        command.Parameters.Add(new SqlParameter("@Delta", SqlDbType.Int) { Value = kind == 'S' ? 1 : -1 });
        command.Parameters.Add(new SqlParameter("@Delivery", SqlDbType.Char, 32) { Value = delivery });
        command.Parameters.Add(new SqlParameter("@Boot", SqlDbType.NVarChar, 150) { Value = Environment.GetEnvironmentVariable("WORKERDOCK_QUARTZ_BOOT")! });
        command.Parameters.Add(new SqlParameter("@Kind", SqlDbType.Char, 1) { Value = kind.ToString() });
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
