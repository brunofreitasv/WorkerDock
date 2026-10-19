using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerDock.Application.Jobs;
using WorkerDock.ControlCenter;
using WorkerDock.ControlCenter.Health;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.IntegrationTests;

public sealed class ControlCenterIsolationTests
{
    [Fact]
    public async Task Center_can_register_and_probe_tenant_without_exposing_credentials_or_tenant_services()
    {
        await using var factory = CreateFactory(authentication: false);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });

        using var created = await SendJsonAsync(client, HttpMethod.Post, "/api/tenants", new
        {
            displayName = "Finance",
            expectedInstanceId = Guid.NewGuid(),
            dashboardUrl = "https://finance.example.test/",
            healthCredentialReference = "FINANCE_HEALTH"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("FINANCE_HEALTH", body, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", body, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(created.Headers.ETag);

        var registration = await created.Content.ReadFromJsonAsync<TenantDirectoryEntry>();
        Assert.NotNull(registration);
        using var refresh = await SendJsonAsync(client, HttpMethod.Post, $"/api/tenants/{registration!.Id}/health/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<RecordingHealthProbe>().Calls);
        Assert.False(factory.Services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IJobCommands)));
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
    }

    [Fact]
    public async Task Manager_can_update_and_remove_a_link_with_optimistic_concurrency()
    {
        await using var factory = CreateFactory(authentication: false);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var created = await SendJsonAsync(client, HttpMethod.Post, "/api/tenants", new
        {
            displayName = "Finance",
            expectedInstanceId = Guid.NewGuid(),
            dashboardUrl = "https://finance.example.test/",
            healthCredentialReference = "FINANCE_HEALTH"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = await created.Content.ReadFromJsonAsync<TenantDirectoryEntry>();
        Assert.NotNull(entry);
        var originalTag = created.Headers.ETag!.Tag;

        using var updated = await SendJsonAsync(client, HttpMethod.Put, $"/api/tenants/{entry!.Id}", new
        {
            displayName = "Finance EU",
            expectedInstanceId = entry.ExpectedInstanceId,
            dashboardUrl = "https://finance.example.test/eu/",
            healthCredentialReference = "FINANCE_HEALTH"
        }, originalTag);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedEntry = await updated.Content.ReadFromJsonAsync<TenantDirectoryEntry>();
        Assert.Equal("Finance EU", updatedEntry!.DisplayName);
        Assert.NotEqual(originalTag, updated.Headers.ETag!.Tag);

        using var stale = await SendJsonAsync(client, HttpMethod.Delete, $"/api/tenants/{entry.Id}", null, originalTag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var removed = await SendJsonAsync(client, HttpMethod.Delete, $"/api/tenants/{entry.Id}", null, updated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var page = await client.GetFromJsonAsync<WorkerDock.Application.Monitoring.Page<TenantDirectoryEntry>>("/api/tenants");
        Assert.Empty(page!.Items);
    }

    [Fact]
    public void Center_requires_an_explicit_health_host_allowlist()
    {
        using var factory = CreateFactory(authentication: false, configureHealthAllowlist: false);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("CentralHealthHostAllowlistRequired", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_reader_can_refresh_but_only_management_role_can_register()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var factory = CreateFactory(authentication: true, issuer, requiredRole: "Manager");
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tenants")).StatusCode);
        await issuer.SignIn(client, authCookieName: "__Host-WorkerDock.Center.Auth");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tenants")).StatusCode);
        using var create = await SendJsonAsync(client, HttpMethod.Post, "/api/tenants", new
        {
            displayName = "Finance",
            expectedInstanceId = Guid.NewGuid(),
            dashboardUrl = "https://finance.example.test/",
            healthCredentialReference = "FINANCE_HEALTH"
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Theory]
    [InlineData("http://finance.example.test/")]
    [InlineData("https://user:password@finance.example.test/")]
    [InlineData("https://finance.example.test/?token=secret")]
    [InlineData("https://finance.example.test/#fragment")]
    public async Task Registration_rejects_unsafe_or_secret_bearing_urls(string url)
    {
        await using var factory = CreateFactory(authentication: false);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var response = await SendJsonAsync(client, HttpMethod.Post, "/api/tenants", new
        {
            displayName = "Finance",
            expectedInstanceId = Guid.NewGuid(),
            dashboardUrl = url,
            healthCredentialReference = "FINANCE_HEALTH"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string path, object? body, string? ifMatch = null)
    {
        using var csrfResponse = await client.GetAsync("/api/antiforgery");
        if (!csrfResponse.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException("Antiforgery token request failed: " + await csrfResponse.Content.ReadAsStringAsync());
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfResponse>();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Headers.TryAddWithoutValidation("X-WorkerDock-CSRF", csrf?.RequestToken);
        request.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request);
    }

    private sealed record CsrfResponse(string? RequestToken);

    private static WebApplicationFactory<ControlCenterMarker> CreateFactory(bool authentication, LocalOidcIssuer? issuer = null,
        string? requiredRole = null, bool configureHealthAllowlist = true)
    {
        const string credentialReference = "CENTER_TEST_SECRET";
        if (authentication) Environment.SetEnvironmentVariable("WORKERDOCK_BOOTSTRAP_" + credentialReference, "synthetic-test-credential");
        var factory = new WebApplicationFactory<ControlCenterMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                ["Central:Adapter"] = "Memory",
                ["Central:InstanceId"] = Guid.NewGuid().ToString(),
                ["Central:ConnectionString"] = "",
                ["Access:Authority"] = authentication ? "https://issuer.example" : null,
                ["Access:ClientId"] = authentication ? "tenant-client" : null,
                ["Access:ClientSecretReference"] = authentication ? credentialReference : null,
                ["Access:RequiredManagementRole"] = requiredRole
                };
                if (configureHealthAllowlist) settings["Central:Health:AllowedHosts:0"] = "finance.example.test";
                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICentralRegistry>();
                services.AddSingleton<ICentralRegistry, InMemoryCentralRegistry>();
                services.RemoveAll<ITenantHealthProbe>();
                services.AddSingleton<RecordingHealthProbe>();
                services.AddSingleton<ITenantHealthProbe>(sp => sp.GetRequiredService<RecordingHealthProbe>());
                if (issuer is not null) services.Configure<OpenIdConnectOptions>("oidc", options => options.Backchannel = new HttpClient(issuer, disposeHandler: false));
            });
        });
        return factory;
    }
}

internal sealed class RecordingHealthProbe : ITenantHealthProbe
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public ValueTask<TenantDirectoryEntry> CheckAsync(Guid registrationId, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        return ValueTask.FromResult(new TenantDirectoryEntry(registrationId, "Finance", Guid.NewGuid(), new Uri("https://finance.example.test/"), "Healthy", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new byte[8]));
    }
}
