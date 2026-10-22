using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.IntegrationTests;

public sealed class MonitoringAccessTests
{
    [Fact]
    public async Task Viewer_can_read_monitoring_routes_but_not_protected_job_configuration()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var host = new AccessHost(issuer, "Manager");
        using var client = host.Client();
        await issuer.SignIn(client);

        using var badJobsPage = await client.GetAsync("/api/monitoring/jobs?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, badJobsPage.StatusCode);
        using var badAuditRange = await client.GetAsync("/api/audit?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, badAuditRange.StatusCode);
        using var invalidDate = await client.GetAsync("/api/monitoring/jobs?fromUtc=not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, invalidDate.StatusCode);
        using var dashboard = await client.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, dashboard.StatusCode);
        Assert.DoesNotContain("ConfigurationJson", await dashboard.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/jobs/{Guid.NewGuid()}/configuration")).StatusCode);
    }

    [Fact]
    public async Task Health_credential_is_separate_from_user_session_and_only_unlocks_health()
    {
        var reference = "HEALTH_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var token = "synthetic-health-token-" + Guid.NewGuid().ToString("N");
        await using var factory = new WebApplicationFactory<WorkerDock.Web.WebAssemblyMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenant:Adapter"] = "Memory",
                ["Tenant:InstanceId"] = Guid.NewGuid().ToString(),
                ["Health:CredentialReference"] = reference
            }));
            builder.ConfigureServices(services => services.AddSingleton<ISecretVault>(_ => new HealthTestVault(reference, token)));
        });
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health/ready")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        request.Headers.Add("X-WorkerDock-Health", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("instanceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        var session = await client.GetFromJsonAsync<SessionView>("/api/session");
        Assert.False(session!.Authenticated);
        Assert.Equal("Anonymous", session.Mode);
    }

    private sealed class HealthTestVault(string expectedReference, string token) : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reference.Name != expectedReference) throw new SecretUnavailableException();
            return ValueTask.FromResult(new SecretValue(token));
        }
    }
}
