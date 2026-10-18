using System.Text.Json;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Infrastructure.Secrets;

/// <summary>Call only in the contained child composition root, using protected deployment configuration.</summary>
public static class SecretVaultRegistration
{
    public static SecretProviderDescriptor EnvironmentMetadata => new(
        "workerdock.environment","1.0.0","Development environment","Approved mappings are protected deployment configuration.",
        JsonSerializer.SerializeToElement(new { type="object",properties=new { },additionalProperties=false }),
        JsonSerializer.SerializeToElement(new { labelKey="providers.environment",descriptionKey="providers.environment.description" }));
    public static ISecretVault CreateDevelopment()
    {
        if (Environment.GetEnvironmentVariable("WORKERDOCK_DEVELOPMENT_VAULT") != "1") return new UnavailableVault();
        try
        {
            var json = Environment.GetEnvironmentVariable("WORKERDOCK_DEVELOPMENT_SECRET_MAP");
            if (json is null || json.Length > 32768) return new UnavailableVault();
            return new EnvironmentSecretVault(JsonSerializer.Deserialize<Dictionary<string,string>>(json) ?? []);
        }
        catch { return new UnavailableVault(); }
    }
    private sealed class UnavailableVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference,CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); throw new SecretUnavailableException(); }
    }
}
