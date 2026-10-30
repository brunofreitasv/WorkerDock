using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
    private readonly Channel<ExecutionEvent> eventQueue = Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(64)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly Task eventPump;
    private long lastHandlerSequence;
    private int handlerFrames;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object lifecycle = new();
    private Task? disposal;
    public ChildHandle(Process process, ContainedProcess containment, NamedPipeServerStream pipe, Task stdout, Task stderr,
        IExecutionEvents events, Guid executionId, Guid attemptId)
    {
        this.process = process; this.containment = containment; this.pipe = pipe; this.stdout = stdout; this.stderr = stderr;
        this.events = events; this.executionId = executionId; this.attemptId = attemptId;
        eventPump = PumpEventsAsync();
        completion = ReadCompletionAsync();
    }
    public async ValueTask<TemplateResult> WaitAsync(CancellationToken ct) => await completion.WaitAsync(ct);
    private async Task<TemplateResult> ReadCompletionAsync()
    {
        TemplateResult result;
        try
        {
            while (true)
            {
                var payload = await ExecutionProtocol.ReadPayloadAsync(pipe, default);
                JsonDocument document;
                try { document = JsonDocument.Parse(payload); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "Diagnostic")
                    {
                        if (payload.Length <= 4096)
                        {
                            try
                            {
                                var diagnostic = JsonSerializer.Deserialize<ExecutorDiagnosticFrame>(payload);
                                if (diagnostic is not null) QueueHandlerDiagnostic(diagnostic);
                            }
                            catch (JsonException) { }
                        }
                        continue;
                    }
                    result = DeserializeResult(payload);
                    break;
                }
            }
            if (result.Outcome is not ("Success" or "TransientFailure" or "PermanentFailure") || !SafeCode(result.SafeCode))
                throw new ExecutionProcessException("IpcInvalidResult");
            result = ValidateFailureDiagnostic(result);
        }
        catch
        {
            result = new("TransientFailure", "ExecutorDisconnected", true, JsonSerializer.SerializeToElement(new { }), null,
                new FailureDiagnostic("Process"));
        }
        // Even a malformed/closed pipe must confirm local tree exit before completion is observable.
        await containment.TerminateAsync(default);
        QueueEvent(new ExecutionEvent(executionId, attemptId, "Supervisor", 1, DateTimeOffset.UtcNow,
            result.Outcome == "Success" ? "TemplateCompleted" : "TemplateFailed",
            new Dictionary<string, string> { ["outcome"] = result.Outcome }));
        eventQueue.Writer.TryComplete();
        return result;
    }
    private void QueueHandlerDiagnostic(ExecutorDiagnosticFrame diagnostic)
    {
        if (diagnostic.Kind != "Diagnostic" || diagnostic.Sequence is <= 0 or > 256 || diagnostic.Sequence <= lastHandlerSequence ||
            handlerFrames >= 256 || !SafeHandlerDiagnostic(diagnostic)) return;
        lastHandlerSequence = diagnostic.Sequence;
        handlerFrames++;
        QueueEvent(new ExecutionEvent(executionId, attemptId, "Handler", diagnostic.Sequence, DateTimeOffset.UtcNow, diagnostic.EventCode,
            diagnostic.SafeFields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
    }
    private static bool SafeHandlerDiagnostic(ExecutorDiagnosticFrame diagnostic)
    {
        if (diagnostic.EventCode is not ("TemplateStarted" or "TemplateCompleted" or "TemplateFailed" or "SecretUnavailable") ||
            diagnostic.SafeFields is null || diagnostic.SafeFields.Count > 8) return false;
        foreach (var (key, value) in diagnostic.SafeFields)
        {
            if (value is not { Length: > 0 and <= 80 }) return false;
            var safe = key switch
            {
                "executionId" or "attemptId" or "correlationId" => Guid.TryParseExact(value, "D", out _),
                "durationMs" or "affectedRows" => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0,
                "outcome" => value is "Success" or "TransientFailure" or "PermanentFailure",
                "safeCode" => value is "Success" or "SecretUnavailable" or "TemplateCancelled" or "TemplateExecutionFailed" or "TemplateConfigurationInvalid" or "ConnectivityFailed" or "AuthenticationFailed" or "DestinationUnavailable" or "CommandFailed" or "Timeout",
                _ => false
            };
            if (!safe) return false;
        }
        return true;
    }
    private void QueueEvent(ExecutionEvent item) => eventQueue.Writer.TryWrite(item);
    private async Task PumpEventsAsync()
    {
        await foreach (var item in eventQueue.Reader.ReadAllAsync())
            try { await events.AppendAsync([item], CancellationToken.None); }
            catch { /* Diagnostic storage is optional and isolated from execution. */ }
    }
    private static bool SafeCode(string? value) => value is { Length: > 0 and <= 80 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static TemplateResult DeserializeResult(byte[] payload)
    {
        try { return JsonSerializer.Deserialize<TemplateResult>(payload) ?? throw new ExecutionProcessException("IpcInvalidResult"); }
        catch (JsonException)
        {
            // A malformed optional envelope must not turn the safe result into a disconnect.
            var node = JsonNode.Parse(payload)?.AsObject() ?? throw new ExecutionProcessException("IpcInvalidResult");
            if (!node.Remove("Diagnostic")) throw;
            return node.Deserialize<TemplateResult>() ?? throw new ExecutionProcessException("IpcInvalidResult");
        }
    }
    private static TemplateResult ValidateFailureDiagnostic(TemplateResult result)
    {
        if (result.Diagnostic is null) return result;
        try
        {
            var diagnostic = result.Diagnostic;
            static bool Token(string? text) => text is { Length: > 0 and <= 80 } &&
                text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
            static bool Text(string? text, int limit) => text is null || JsonSerializer.SerializeToUtf8Bytes(text).Length - 2 <= limit;
            var valid = result.Outcome != "Success" && Token(diagnostic.Kind) &&
                Text(diagnostic.ExceptionType, 512) && Text(diagnostic.Message, 4096) &&
                Text(diagnostic.StackTrace, 8192) && Text(diagnostic.ResponseContent, 16384) &&
                Text(diagnostic.SqlProcedure, 512) && Text(diagnostic.CommandKind, 80) &&
                Text(diagnostic.HttpMethod, 32) && Text(diagnostic.MediaType, 128) &&
                diagnostic.SqlState is null or >= 0 and <= 255 &&
                diagnostic.SqlLine is null or >= 0 && diagnostic.HttpStatus is null or >= 100 and <= 599 &&
                diagnostic.ResponseBytes is null or >= 0 &&
                (!diagnostic.Hidden || diagnostic is { Message: null, StackTrace: null, ResponseContent: null }) &&
                JsonSerializer.SerializeToUtf8Bytes(diagnostic).Length <= 32 * 1024 &&
                JsonSerializer.SerializeToUtf8Bytes(result).Length <= 48 * 1024;
            return valid ? result : result with { Diagnostic = null };
        }
        catch { return result with { Diagnostic = null }; }
    }
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
                eventQueue.Writer.TryComplete();
                try { await eventPump.WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { }
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
