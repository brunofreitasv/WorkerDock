using WorkerDock.Infrastructure.Templates;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Values;
using WorkerDock.Runtime.Ipc;
namespace WorkerDock.Executor;

public sealed record ExecutionAdapters(ISecretVault Secrets, ISafeExecutionLog Log);

/// <summary>Installed composition root only; factories are invoked after containment permission.</summary>
public static class ExecutorHost
{
    public static async Task<int> RunAsync(Func<ExecutionDescriptor, IReadOnlyList<IWorkerTemplate>> templates, Func<ExecutionDescriptor, ExecutionAdapters> adapters)
    {
        try
        {
            var name = Required("WORKERDOCK_PIPE"); var nonce = Required("WORKERDOCK_NONCE");
            var attempt = Guid.Parse(Required("WORKERDOCK_ATTEMPT"));
            var generation = long.Parse(Required("WORKERDOCK_GENERATION"), CultureInfo.InvariantCulture);
            var parent = int.Parse(Required("WORKERDOCK_PARENT"), CultureInfo.InvariantCulture);
            // Do not expose bootstrap capabilities to installed handlers or their descendants.
            foreach (var key in new[] { "WORKERDOCK_PIPE", "WORKERDOCK_NONCE", "WORKERDOCK_ATTEMPT", "WORKERDOCK_GENERATION", "WORKERDOCK_PARENT" }) Environment.SetEnvironmentVariable(key, null);
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(startup.Token);
            if (!ExecutionProtocol.ServerIs(pipe, parent)) return 1;
            await ExecutionProtocol.WriteAsync(pipe, new Handshake(ExecutionProtocol.CurrentVersion, attempt, generation, nonce, Environment.ProcessId), startup.Token);
            var request = await ExecutionProtocol.ReadAsync<ExecuteRequest>(pipe, startup.Token);
            if (request.Version != ExecutionProtocol.CurrentVersion || request.AttemptId != attempt || request.Generation != generation) return 1;
            using var writer = new SemaphoreSlim(1, 1);
            var composedAdapters = adapters;
            await using var diagnostics = new IpcDiagnosticRelay(pipe, writer, null);
            using var cancellation = new CancellationTokenSource();
            using var controls = new CancellationTokenSource();
            var listener = ListenAsync(pipe, cancellation, controls.Token);
            var result = await ExecuteAsync(request, templates, composedAdapters, diagnostics, cancellation.Token);
            await diagnostics.FlushAsync();
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await writer.WaitAsync(finish.Token);
            try { await ExecutionProtocol.WriteAsync(pipe, result, finish.Token); }
            finally { writer.Release(); }
            controls.Cancel(); await listener;
            return 0;
        }
        catch { return 1; } // Never print raw handler/provider/bootstrap exceptions.
    }
    private static async Task ListenAsync(Stream pipe, CancellationTokenSource cancellation, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var control = await ExecutionProtocol.ReadAsync<ExecutionControl>(pipe, ct);
                if (control.Kind != "Cancel") { cancellation.Cancel(); return; }
                cancellation.Cancel();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { cancellation.Cancel(); }
    }
    private static async Task<TemplateResult> ExecuteAsync(ExecuteRequest request, Func<ExecutionDescriptor, IReadOnlyList<IWorkerTemplate>> templates, Func<ExecutionDescriptor, ExecutionAdapters> adapters, IpcDiagnosticRelay diagnostics, CancellationToken ct)
    {
        using var secrets = new RedactingSecrets();
        try
        {
            if (!ExecutionPayloadLimits.FitsPreparedDescriptor(request.Execution)) return Failure("IpcMessageTooLarge", false);
            var resolutionError = request.Execution.ValueResolutionError;
            if (resolutionError is not null)
            {
                if (request.Execution.ValueSnapshot is not null) return Failure("DynamicSnapshotInvalid", false);
                return Failure(SafeResolutionCode(resolutionError) ? resolutionError : "DynamicSnapshotInvalid", false);
            }
            if (request.Execution.ValueContext is null && request.Execution.ValueSnapshot is not null ||
                request.Execution.ValueContext is not null && request.Execution.ValueSnapshot is null)
                return Failure("DynamicSnapshotInvalid", false);
            var installed = templates(request.Execution);
            if (installed.GroupBy(x => (x.Descriptor.Id, x.Descriptor.Version)).Any(x => x.Count() > 1)) return Failure("TemplateRegistrationConflict", false);
            var template = installed.SingleOrDefault(x => x.Descriptor.Id == request.Execution.TemplateId && x.Descriptor.Version == request.Execution.TemplateVersion);
            if (template is null) return Failure("TemplateVersionUnavailable", false);
            if (string.IsNullOrEmpty(request.MetadataDigest) || template.Descriptor.MetadataDigest != request.MetadataDigest) return Failure("TemplateMetadataMismatch", false);
            if (request.Execution.ValueSnapshot is not null && !DynamicValueValidation.ValidateResolvedSnapshot(template.Descriptor, request.Execution))
                return Failure("DynamicSnapshotInvalid", false);
            try
            {
                if (TemplateSchemaValidator.Validate(template.Descriptor,request.Execution.Configuration).Count != 0 ||
                    template.Validate(request.Execution.Configuration).Count != 0) return Failure("TemplateConfigurationInvalid", false);
            }
            catch { return Failure("TemplateConfigurationInvalid", false); }
            ct.ThrowIfCancellationRequested();
            var composed = adapters(request.Execution);
            secrets.Provider = composed.Secrets;
            diagnostics.SetTarget(composed.Log);
            var context = new TemplateExecutionContext(request.Execution, secrets, new RedactingLog(secrets, diagnostics));
            var result = await template.ExecuteAsync(context, ct);
            if (result.Outcome is not ("Success" or "TransientFailure" or "PermanentFailure") || !Code(result.SafeCode)) return Failure("TemplateResultInvalid", true);
            var safe = secrets.Redact(JsonSerializer.Serialize(result.SafeSummary));
            using var document = JsonDocument.Parse(safe);
            var sanitized = result with { SafeCode = secrets.Redact(result.SafeCode), SafeSummary = document.RootElement.Clone() };
            _ = ExecutionProtocol.Serialize(sanitized);
            return sanitized;
        }
        catch (SecretUnavailableException) { return Failure("SecretUnavailable", true); }
        catch (OperationCanceledException) { return Failure("TemplateCancelled", true); }
        catch { return Failure("TemplateExecutionFailed", true); }
    }
    private static TemplateResult Failure(string code, bool effects) => new("PermanentFailure", code, effects, JsonSerializer.SerializeToElement(new { }), null);
    private static bool SafeResolutionCode(string code) => code is
        "DynamicBindingInvalid" or "DynamicResolverUnsupported" or "DynamicTimeZoneInvalid" or
        "DynamicTypeMismatch" or "DynamicSourceUnavailable" or "DynamicDateOutOfRange" or
        "DynamicLocalTimeInvalid" or "DynamicSnapshotInvalid" or "DynamicSnapshotTooLarge" or
        "DynamicTargetInvalid";
    private static bool Code(string? value) => value is { Length: > 0 and <= 80 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static string Required(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException();

    private sealed class RedactingSecrets : ISecretVault, IDisposable
    {
        private readonly List<char[]> resolved = [];
        private readonly List<SecretValue> leased = [];
        private readonly object gate = new();
        public ISecretVault Provider { get; set; } = null!;
        public async ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SecretValue? value=null;
            try
            {
                value=await Provider.ResolveAsync(reference,ct);
                var snapshot=value.Reveal().ToArray();
                lock(gate) { resolved.Add(snapshot); leased.Add(value); }
                return value;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { value?.Dispose(); throw new OperationCanceledException(ct); }
            catch { value?.Dispose(); throw new SecretUnavailableException(); }
        }
        public string Redact(string text)
        {
            lock (gate)
                foreach (var value in resolved)
                    if (value.Length > 0)
                    {
                        var secret = new string(value);
                        text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                        // JSON escaping must not bypass redaction in a structured result.
                        var escaped = JsonSerializer.Serialize(secret);
                        text = text.Replace(escaped[1..^1], "[REDACTED]", StringComparison.Ordinal);
                    }
            return text;
        }
        public void Dispose() { lock (gate) { foreach (var value in leased) value.Dispose(); leased.Clear(); foreach (var value in resolved) Array.Clear(value); resolved.Clear(); } }
    }
    private sealed class RedactingLog(RedactingSecrets secrets, IpcDiagnosticRelay diagnostics) : ISafeExecutionLog
    {
        private int accepted;
        public async ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || safeFields is null || safeFields.Count > 32 || Interlocked.Increment(ref accepted) > 256) return;
            try
            {
                var redactedCode = secrets.Redact(eventCode);
                var redactedFields = safeFields.ToDictionary(x => secrets.Redact(x.Key), x => secrets.Redact(x.Value), StringComparer.Ordinal);
                diagnostics.TryWrite(redactedCode, redactedFields);
            }
            catch { /* Optional diagnostics cannot escape into the handler. */ }
        }
    }

    private sealed class IpcDiagnosticRelay : IAsyncDisposable
    {
        private static readonly HashSet<string> Events = new(StringComparer.Ordinal) { "TemplateStarted", "TemplateCompleted", "TemplateFailed", "SecretUnavailable" };
        private static readonly HashSet<string> Codes = new(StringComparer.Ordinal) { "Success", "SecretUnavailable", "TemplateCancelled", "TemplateExecutionFailed", "TemplateConfigurationInvalid", "ConnectivityFailed", "AuthenticationFailed", "DestinationUnavailable", "CommandFailed", "Timeout" };
        private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "executionId", "attemptId", "correlationId", "durationMs", "affectedRows", "outcome", "safeCode" };
        private readonly Stream pipe;
        private readonly SemaphoreSlim writer;
        private ISafeExecutionLog? target;
        private readonly Channel<ExecutorDiagnosticFrame> queue = Channel.CreateBounded<ExecutorDiagnosticFrame>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        private readonly CancellationTokenSource stop = new();
        private readonly Task pump;
        private long sequence;
        private int accepted;

        public IpcDiagnosticRelay(Stream pipe, SemaphoreSlim writer, ISafeExecutionLog? target)
        {
            this.pipe = pipe; this.writer = writer; this.target = target; pump = PumpAsync();
        }

        public void SetTarget(ISafeExecutionLog log) => target = log;

        public void TryWrite(string eventCode, IReadOnlyDictionary<string, string> fields)
        {
            if (!Events.Contains(eventCode) || fields.Count > 8 || Interlocked.Increment(ref accepted) > 256) return;
            foreach (var (key, value) in fields)
            {
                if (!Fields.Contains(key) || value is not { Length: > 0 and <= 80 }) return;
                var valid = key switch
                {
                    "executionId" or "attemptId" or "correlationId" => Guid.TryParseExact(value, "D", out _),
                    "durationMs" or "affectedRows" => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0,
                    "outcome" => value is "Success" or "TransientFailure" or "PermanentFailure",
                    "safeCode" => Codes.Contains(value),
                    _ => false
                };
                if (!valid) return;
            }
            var frame = new ExecutorDiagnosticFrame("Diagnostic", Interlocked.Increment(ref sequence), eventCode, new Dictionary<string, string>(fields, StringComparer.Ordinal));
            try { if (ExecutionProtocol.Serialize(frame).Length <= 4096) queue.Writer.TryWrite(frame); }
            catch { }
        }

        private async Task PumpAsync()
        {
            await foreach (var frame in queue.Reader.ReadAllAsync(stop.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
                try { if (target is not null) await target.WriteAsync(frame.EventCode, frame.SafeFields, timeout.Token); } catch { }
                if (timeout.IsCancellationRequested) continue;
                try
                {
                    await writer.WaitAsync(timeout.Token);
                    try { await ExecutionProtocol.WriteAsync(pipe, frame, timeout.Token); }
                    finally { writer.Release(); }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                catch { /* A broken diagnostic path only drops this optional frame. */ }
            }
        }

        public async ValueTask FlushAsync()
        {
            queue.Writer.TryComplete();
            try { await pump.WaitAsync(TimeSpan.FromMilliseconds(250)); }
            catch { stop.Cancel(); try { await pump.WaitAsync(TimeSpan.FromMilliseconds(100)); } catch { } }
        }

        public async ValueTask DisposeAsync()
        {
            queue.Writer.TryComplete();
            try { await pump.WaitAsync(TimeSpan.FromMilliseconds(250)); }
            catch { stop.Cancel(); try { await pump.WaitAsync(TimeSpan.FromMilliseconds(100)); } catch { } }
            stop.Dispose();
        }
    }
}
