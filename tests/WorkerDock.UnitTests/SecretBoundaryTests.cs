using System.Text.Json;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Infrastructure.Logging;
namespace WorkerDock.UnitTests;
public sealed class SecretBoundaryTests
{
    [Fact]
    public async Task Rotation_and_provider_failure_preserve_redaction()
    {
        const string oldSentinel = "OLD_SYNTHETIC_SECRET_641";
        const string newSentinel = "NEW_SYNTHETIC_SECRET_842";
        string? current = oldSentinel;
        var vault = new EnvironmentSecretVault(new Dictionary<string,string> { ["database"] = "TENANT_DATABASE_PASSWORD" }, _ => current);
        using var first = await vault.ResolveAsync(new("database"), default);
        Assert.Equal("[REDACTED]", first.ToString());
        Assert.Equal(oldSentinel, first.Reveal().ToString());
        current = newSentinel;
        using var second = await vault.ResolveAsync(new("database"), default);
        Assert.Equal(newSentinel, second.Reveal().ToString());
        var broken = new EnvironmentSecretVault(new Dictionary<string,string> { ["database"] = "TENANT_DATABASE_PASSWORD" }, _ => throw new Exception(oldSentinel + newSentinel));
        var failure = await Assert.ThrowsAsync<SecretUnavailableException>(async () => await broken.ResolveAsync(new("database"), default));
        Assert.Equal("SecretUnavailable", failure.SafeCode);
        var diagnostics = failure.ToString() + JsonSerializer.Serialize(new { failure.SafeCode });
        Assert.DoesNotContain(oldSentinel, diagnostics);
        Assert.DoesNotContain(newSentinel, diagnostics);
        Assert.Null(failure.InnerException);
        Assert.Equal("SecretUnavailable", (await Assert.ThrowsAsync<SecretUnavailableException>(async () => await vault.ResolveAsync(new("not-approved"),default))).SafeCode);
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Reveal());
    }
    [Fact]
    public async Task Log_rejects_arbitrary_values_and_projects_only_bounded_safe_fields()
    {
        var emitted = new List<string>();
        var log = new SafeExecutionLog(emitted.Add);
        await log.WriteAsync("TemplateCompleted",new Dictionary<string,string> { ["outcome"]="Success", ["durationMs"]="12" },default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await log.WriteAsync("TemplateFailed",new Dictionary<string,string> { ["message"]="SECRET_SENTINEL" },default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await log.WriteAsync("TemplateFailed",new Dictionary<string,string> { ["safeCode"]="SECRET_SENTINEL" },default));
        Assert.Single(emitted);
        Assert.DoesNotContain("SECRET_SENTINEL",string.Join("",emitted));
    }
    [Fact]
    public void Installed_provider_metadata_accepts_no_browser_authentication_or_environment_overrides()
    {
        var metadata=SecretVaultRegistration.EnvironmentMetadata;
        Assert.Empty(WorkerDock.Infrastructure.Templates.TemplateSchemaValidator.ValidateProviderOptions(metadata,JsonSerializer.SerializeToElement(new {})));
        var issues=WorkerDock.Infrastructure.Templates.TemplateSchemaValidator.ValidateProviderOptions(metadata,JsonSerializer.SerializeToElement(new { password="SENTINEL",environmentVariable="PATH" }));
        Assert.Contains(issues,x=>x.Code=="AdditionalProperty");
        Assert.DoesNotContain("SENTINEL",JsonSerializer.Serialize(issues));
    }
}
