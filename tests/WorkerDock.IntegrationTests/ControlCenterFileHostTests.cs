using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.ControlCenter;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.IntegrationTests;

public sealed class ControlCenterFileHostTests
{
    [Fact]
    public async Task File_backed_center_starts_without_sql_and_keeps_links_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkerDock-Center-" + Guid.NewGuid().ToString("N"));
        var instanceId = Guid.NewGuid();
        try
        {
            await using (var first = CreateFactory(root, instanceId))
            using (var client = first.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
            {
                using var readiness = await client.GetAsync("/health/ready");
                Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);

                using var csrfResponse = await client.GetAsync("/api/antiforgery");
                var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfResponse>();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenants")
                {
                    Content = JsonContent.Create(new
                    {
                        displayName = "Finance",
                        expectedInstanceId = Guid.NewGuid(),
                        dashboardUrl = "https://finance.example.test/",
                        healthCredentialReference = "FINANCE_HEALTH"
                    })
                };
                request.Headers.TryAddWithoutValidation("X-WorkerDock-CSRF", csrf?.RequestToken);
                request.Headers.TryAddWithoutValidation("Origin", "https://localhost");
                using var created = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            await using (var restarted = CreateFactory(root, instanceId))
            using (var client = restarted.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }))
            {
                var page = await client.GetFromJsonAsync<WorkerDock.Application.Monitoring.Page<WorkerDock.ControlCenter.Registry.TenantDirectoryEntry>>("/api/tenants");
                Assert.Equal("Finance", Assert.Single(page!.Items).DisplayName);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Production_center_rejects_memory_adapter()
    {
        using var factory = new WebApplicationFactory<ControlCenterMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Central:InstanceId"] = Guid.NewGuid().ToString(),
                ["Central:Adapter"] = "Memory",
                ["Central:Health:AllowedHosts:0"] = "finance.example.test"
            }));
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("CentralMemoryAdapterDevelopmentOnly", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_endpoint_returns_503_when_registry_storage_is_unavailable()
    {
        using var factory = new WebApplicationFactory<ControlCenterMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Central:InstanceId"] = Guid.NewGuid().ToString(),
                ["Central:Adapter"] = "Memory",
                ["Central:Health:AllowedHosts:0"] = "finance.example.test"
            })).ConfigureServices(services =>
            {
                services.RemoveAll<ICentralRegistry>();
                services.AddSingleton<ICentralRegistry, UnavailableRegistry>();
            });
        });

        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static WebApplicationFactory<ControlCenterMarker> CreateFactory(string dataDirectory, Guid instanceId) =>
        new WebApplicationFactory<ControlCenterMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Central:InstanceId"] = instanceId.ToString(),
                ["Central:Storage:DataDirectory"] = dataDirectory,
                ["Central:Health:AllowedHosts:0"] = "finance.example.test"
            }));
        });

    private sealed record CsrfResponse(string? RequestToken);

    private sealed class UnavailableRegistry : ICentralRegistry
    {
        public ValueTask<Page<TenantDirectoryEntry>> ListAsync(string? search, PageRequest page, CancellationToken ct) =>
            ValueTask.FromException<Page<TenantDirectoryEntry>>(new WorkerDockException("PersistenceUnavailable"));
        public ValueTask<TenantRegistration?> GetAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public ValueTask<TenantDirectoryEntry> SaveAsync(Guid? id, TenantRegistrationInput input, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) => throw new NotImplementedException();
        public ValueTask RemoveAsync(Guid id, byte[] expectedVersion, ActorContext actor, CancellationToken ct) => throw new NotImplementedException();
        public ValueTask<TenantDirectoryEntry> RecordHealthAsync(Guid id, string health, string? safeCode, DateTimeOffset attemptedUtc, DateTimeOffset? succeededUtc, CancellationToken ct) => throw new NotImplementedException();
    }
}
