using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WorkerDock.Application.Execution;
using WorkerDock.Contracts.Templates;
using WorkerDock.Runtime.Ipc;
namespace WorkerDock.Runtime.Processes;

internal sealed class ChildHandle : IExecutionChild
{
    private readonly Process process;
    private readonly ContainedProcess containment;
    private readonly NamedPipeServerStream pipe;
    private readonly Task stdout, stderr;
    private readonly Task<TemplateResult> completion;
    private readonly SemaphoreSlim writes = new(1, 1);
    private int disposed;
    public ChildHandle(Process process, ContainedProcess containment, NamedPipeServerStream pipe, Task stdout, Task stderr)
    {
        this.process = process; this.containment = containment; this.pipe = pipe; this.stdout = stdout; this.stderr = stderr;
        completion = ReadCompletionAsync();
    }
    public async ValueTask<TemplateResult> WaitAsync(CancellationToken ct) => await completion.WaitAsync(ct);
    private async Task<TemplateResult> ReadCompletionAsync()
    {
        TemplateResult result;
        try
        {
            result = await ExecutionProtocol.ReadAsync<TemplateResult>(pipe, default);
            if (result.Outcome is not ("Success" or "TransientFailure" or "PermanentFailure") || !SafeCode(result.SafeCode))
                throw new ExecutionProcessException("IpcInvalidResult");
        }
        catch
        {
            result = new("TransientFailure", "ExecutorDisconnected", true, JsonSerializer.SerializeToElement(new { }), null);
        }
        // Even a malformed/closed pipe must confirm local tree exit before completion is observable.
        await containment.TerminateAsync(default);
        return result;
    }
    private static bool SafeCode(string? value) => value is { Length: > 0 and <= 80 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    public async ValueTask CancelAsync(CancellationToken ct)
    {
        if (completion.IsCompleted) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await writes.WaitAsync(timeout.Token);
        try { if (!completion.IsCompleted) await ExecutionProtocol.WriteAsync(pipe, new ExecutionControl("Cancel"), timeout.Token); }
        catch (IOException) { }
        finally { writes.Release(); }
    }
    public ValueTask TerminateAsync(CancellationToken ct) => containment.TerminateAsync(ct);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await containment.DisposeAsync(); }
        finally
        {
            await pipe.DisposeAsync();
            await completion;
            await Task.WhenAll(stdout, stderr);
            process.Dispose(); writes.Dispose();
        }
    }
}
