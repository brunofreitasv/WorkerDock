using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Infrastructure.Logging;
namespace WorkerDock.Executor;

internal static class Program
{
    public static Task<int> Main() => ExecutorHost.RunAsync(
        // Add only explicit references to reviewed installed packages. No discovery from job input.
        () => [],
        _ => new(SecretVaultRegistration.CreateDevelopment(),new SafeExecutionLog(line => System.Diagnostics.Trace.WriteLine(line))));
}
