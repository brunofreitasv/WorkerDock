using System.Threading.Channels;
using WorkerDock.Application.Observability;

namespace WorkerDock.Infrastructure.Observability;

/// <summary>Bounded, best-effort relay so diagnostic persistence cannot delay an execution result.</summary>
public sealed class BufferedExecutionEvents : IExecutionEvents, IAsyncDisposable
{
    private readonly IExecutionEvents sink;
    private readonly Channel<ExecutionEvent[]> queue;
    private readonly Task pump;
    private long dropped;

    public BufferedExecutionEvents(IExecutionEvents sink, int capacity = 128)
    {
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.sink = sink;
        queue = Channel.CreateBounded<ExecutionEvent[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        pump = Task.Run(PumpAsync);
    }

    public long DroppedCount => Interlocked.Read(ref dropped);

    public ValueTask AppendAsync(IReadOnlyList<ExecutionEvent> events, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || events.Count is < 1 or > 100)
        {
            Interlocked.Add(ref dropped, Math.Max(events.Count, 1));
            return ValueTask.CompletedTask;
        }
        if (!queue.Writer.TryWrite(events.ToArray())) Interlocked.Add(ref dropped, events.Count);
        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync()
    {
        await foreach (var batch in queue.Reader.ReadAllAsync())
        {
            try { await sink.AppendAsync(batch, CancellationToken.None); }
            catch { Interlocked.Add(ref dropped, batch.Length); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { Interlocked.Add(ref dropped, 1); }
    }
}
