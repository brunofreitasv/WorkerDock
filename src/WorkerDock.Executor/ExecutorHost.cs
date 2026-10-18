using WorkerDock.Infrastructure.Templates;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
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
            await ExecutionProtocol.WriteAsync(pipe, new Handshake(1, attempt, generation, nonce, Environment.ProcessId), startup.Token);
            var request = await ExecutionProtocol.ReadAsync<ExecuteRequest>(pipe, startup.Token);
            if (request.Version != 1 || request.AttemptId != attempt || request.Generation != generation) return 1;
            using var cancellation = new CancellationTokenSource();
            using var controls = new CancellationTokenSource();
            var listener = ListenAsync(pipe, cancellation, controls.Token);
            var result = await ExecuteAsync(request, templates, adapters, cancellation.Token);
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ExecutionProtocol.WriteAsync(pipe, result, finish.Token);
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
    private static async Task<TemplateResult> ExecuteAsync(ExecuteRequest request, Func<ExecutionDescriptor, IReadOnlyList<IWorkerTemplate>> templates, Func<ExecutionDescriptor, ExecutionAdapters> adapters, CancellationToken ct)
    {
        using var secrets = new RedactingSecrets();
        try
        {
            var installed = templates(request.Execution);
            if (installed.GroupBy(x => (x.Descriptor.Id, x.Descriptor.Version)).Any(x => x.Count() > 1)) return Failure("TemplateRegistrationConflict", false);
            var template = installed.SingleOrDefault(x => x.Descriptor.Id == request.Execution.TemplateId && x.Descriptor.Version == request.Execution.TemplateVersion);
            if (template is null) return Failure("TemplateVersionUnavailable", false);
            if (string.IsNullOrEmpty(request.MetadataDigest) || template.Descriptor.MetadataDigest != request.MetadataDigest) return Failure("TemplateMetadataMismatch", false);
            if (TemplateSchemaValidator.Validate(template.Descriptor,request.Execution.Configuration).Count != 0 || template.Validate(request.Execution.Configuration).Count != 0) return Failure("TemplateConfigurationInvalid", false);
            ct.ThrowIfCancellationRequested();
            var composed = adapters(request.Execution);
            secrets.Provider = composed.Secrets;
            var context = new TemplateExecutionContext(request.Execution, secrets, new RedactingLog(composed.Log, secrets));
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
    private sealed class RedactingLog(ISafeExecutionLog target, RedactingSecrets secrets) : ISafeExecutionLog
    {
        public ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct)
        {
            if (!Code(eventCode) || safeFields.Count > 32 || safeFields.Any(x => !Code(x.Key) || x.Value.Length > 1024)) throw new InvalidOperationException("SafeLogInvalid");
            return target.WriteAsync(secrets.Redact(eventCode), safeFields.ToDictionary(x => secrets.Redact(x.Key), x => secrets.Redact(x.Value), StringComparer.Ordinal), ct);
        }
    }
}
