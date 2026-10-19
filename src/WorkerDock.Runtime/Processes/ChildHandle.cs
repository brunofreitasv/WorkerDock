using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WorkerDock.Application.Execution;
using WorkerDock.Application.Observability;
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
    private readonly IExecutionEvents events;
    private readonly Guid executionId, attemptId;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object lifecycle = new();
    private Task? disposal;
    public ChildHandle(Process process, ContainedProcess containment, NamedPipeServerStream pipe, Task stdout, Task stderr,
        IExecutionEvents events, Guid executionId, Guid attemptId)
    {
        this.process = process; this.containment = containment; this.pipe = pipe; this.stdout = stdout; this.stderr = stderr;
        this.events = events; this.executionId = executionId; this.attemptId = attemptId;
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
        _ = RecordOutcomeAsync(result);
        return result;
    }
    private async Task RecordOutcomeAsync(TemplateResult result)
    {
        try
        {
            var eventCode = result.Outcome == "Success" ? "TemplateCompleted" : "TemplateFailed";
            await events.AppendAsync([new ExecutionEvent(executionId, attemptId, "Supervisor", 1, DateTimeOffset.UtcNow, eventCode,
                new Dictionary<string, string> { ["outcome"] = result.Outcome })], CancellationToken.None);
        }
        catch { /* Optional event persistence cannot delay or change the child result. */ }
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
    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new ValueTask(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try { await containment.DisposeAsync(); }
        finally
        {
            try { await pipe.DisposeAsync(); }
            finally
            {
                try { await Task.WhenAll(completion, stdout, stderr); }
                finally
                {
                    try { process.Dispose(); }
                    finally { writes.Dispose(); }
                }
            }
        }
    }
}
