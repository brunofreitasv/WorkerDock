using System.Globalization;
using global::Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;

namespace WorkerDock.Quartz.Dispatch;

[RetryPolicy(0)]
public sealed class ExecuteTemplateJob(IAttemptStore store, IAttemptRunner runner, WorkerIdentity worker, TimeProvider clock) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var intent = Decode(context.JobDetail.JobDataMap);
        var started = clock.GetTimestamp();
        var admission = await store.BeginAsync(intent, worker.BootId, cancellationToken);
        if (admission.Decision != "Started" || admission.Lease is not { } lease) return;
        global::WorkerDock.Contracts.Execution.ExecutionDescriptor descriptor;
        try { descriptor = await store.GetDescriptorAsync(lease, cancellationToken); }
        catch
        {
            // No child call has happened. A persisted stop still wins inside completion.
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.CompleteAsync(lease, new(null, "ProcessFailed", true, false), bounded.Token);
            throw;
        }
        await runner.RunAsync(lease, descriptor, started, cancellationToken);
    }

    private static DispatchIntent Decode(JobDataMap data)
    {
        try
        {
            if (data.Count != 8 || data.Any(pair => pair.Value is not string)) throw new FormatException();
            var intent = new DispatchIntent(Guid.ParseExact(data.GetString("DeliveryId")!, "N"), Guid.ParseExact(data.GetString("ExecutionId")!, "N"),
                int.Parse(data.GetString("AttemptOrdinal")!, CultureInfo.InvariantCulture), data.GetString("TemplateId")!,
                new DateTimeOffset(long.Parse(data.GetString("DueUtc")!, CultureInfo.InvariantCulture), TimeSpan.Zero), 0,
                long.Parse(data.GetString("EntityRevision")!, CultureInfo.InvariantCulture), data.GetString("PayloadDigest")!, long.Parse(data.GetString("CapacityRevision")!, CultureInfo.InvariantCulture));
            if (intent.PayloadDigest != intent.ComputeDigest()) throw new FormatException();
            return intent;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException or KeyNotFoundException)
        { throw new WorkerDockException("DispatchIntegrityError"); }
    }
}
