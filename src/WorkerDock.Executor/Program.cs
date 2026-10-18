using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Infrastructure.Logging;
using WorkerDock.Infrastructure.Outbound;
using WorkerDock.Templates.Http;
using WorkerDock.Application.Outbound;
namespace WorkerDock.Executor;

internal static class Program
{
    public static Task<int> Main() => ExecutorHost.RunAsync(
        // Add only explicit references to reviewed installed packages. No discovery from job input.
        descriptor =>
        {
            var policy = new OutboundPolicy();
            if (descriptor.IntegrationConfiguration.ValueKind == System.Text.Json.JsonValueKind.Object && descriptor.IntegrationConfiguration.TryGetProperty("version", out _))
                policy = OutboundPolicy.FromIntegrationConfiguration(descriptor.IntegrationConfiguration);
            var client = new ValidatedConnectionFactory(policy).CreateClient("tenant-jobs");
            return [new WorkerDock.Templates.SqlServer.SqlTemplate(), new HttpTemplate(client, async (uri, ct) =>
            {
                try { await policy.ResolveAsync(new(uri, "tenant-jobs"), ct); }
                catch (OutboundPolicyException error) { throw new HttpTemplate.HttpTemplateException(error.SafeCode); }
            })];
        },
        _ => new(SecretVaultRegistration.CreateDevelopment(),new SafeExecutionLog(line => System.Diagnostics.Trace.WriteLine(line))));
}
