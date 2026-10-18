using System.Text.Json;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Executor;

internal static class Program
{
    public static Task<int> Main() => ExecutorHost.RunAsync(
        // Only explicit references to reviewed installed packages belong here. No discovery from job input.
        () => [],
        _ => new(new DevelopmentEnvironmentVault(), new SafeDiagnosticLog()));

    private sealed class DevelopmentEnvironmentVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.GetEnvironmentVariable("WORKERDOCK_DEVELOPMENT_VAULT") != "1" || reference.Name.Length is < 1 or > 80 ||
                !reference.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new InvalidOperationException("SecretProviderUnavailable");
            var value = Environment.GetEnvironmentVariable("WORKERDOCK_SECRET_" + reference.Name) ?? throw new InvalidOperationException("SecretUnavailable");
            return ValueTask.FromResult(new SecretValue(value.AsSpan()));
        }
    }
    private sealed class SafeDiagnosticLog : ISafeExecutionLog
    {
        public ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Optional diagnostics only. Raw standard streams are always discarded by the supervisor.
            System.Diagnostics.Trace.WriteLine(JsonSerializer.Serialize(new { eventCode, safeFields }));
            return ValueTask.CompletedTask;
        }
    }
}
