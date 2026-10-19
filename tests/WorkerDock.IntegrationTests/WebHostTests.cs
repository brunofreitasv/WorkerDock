using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace WorkerDock.IntegrationTests;

public sealed class WebHostTests
{
    [Fact]
    public async Task Development_memory_instance_supports_versioned_settings_updates()
    {
        var instanceId = Guid.NewGuid();
        await using var factory = new WebApplicationFactory<WorkerDock.Web.WebAssemblyMarker>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tenant:Adapter"] = "Memory",
                    ["Tenant:InstanceId"] = instanceId.ToString(),
                    ["Tenant:ConnectionString"] = ""
                })));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        using var getOriginal = await client.GetAsync("/api/settings");
        var original = await getOriginal.Content.ReadFromJsonAsync<SettingsResponse>();
        Assert.NotNull(original);
        Assert.Equal('"' + original.Version + '"', getOriginal.Headers.ETag?.ToString());
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(new { displayName = "Equipe", displayTimeZone = "America/Sao_Paulo" })
        };
        update.Headers.TryAddWithoutValidation("If-Match", '"' + original.Version + '"');
        var csrf = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        update.Headers.TryAddWithoutValidation("X-WorkerDock-CSRF", csrf);
        update.Headers.TryAddWithoutValidation("Origin", "https://localhost");
        using var response = await client.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        var changed = await client.GetFromJsonAsync<SettingsResponse>("/api/settings");
        Assert.Equal("Equipe", changed?.DisplayName);
        Assert.NotEqual(original.Version, changed?.Version);
        using var stale = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(new { displayName = "Stale", displayTimeZone = "UTC" })
        };
        stale.Headers.TryAddWithoutValidation("If-Match", '"' + original.Version + '"');
        stale.Headers.TryAddWithoutValidation("X-WorkerDock-CSRF", csrf);
        stale.Headers.TryAddWithoutValidation("Origin", "https://localhost");
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        Assert.Equal("application/problem+json", staleResponse.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await staleResponse.Content.ReadAsStringAsync());
        Assert.Equal("StaleVersion", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("headers.ifMatch", problem.RootElement.GetProperty("field").GetString());

        using var invalid = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
        {
            Content = JsonContent.Create(new { displayName = "", displayTimeZone = "UTC" })
        };
        invalid.Headers.TryAddWithoutValidation("If-Match", '"' + changed!.Version + '"');
        invalid.Headers.TryAddWithoutValidation("X-WorkerDock-CSRF", csrf);
        invalid.Headers.TryAddWithoutValidation("Origin", "https://localhost");
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal("application/problem+json", invalidResponse.Content.Headers.ContentType?.MediaType);
        using var validation = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
        Assert.Equal("ValidationFailed", validation.RootElement.GetProperty("code").GetString());
        Assert.Equal("displayName", validation.RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Liveness_is_safe_and_readiness_fails_closed_without_database_configuration()
    {
        await using var factory = new WebApplicationFactory<WorkerDock.Web.WebAssemblyMarker>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Tenant:ConnectionString"] = "", ["Tenant:InstanceId"] = "", ["DataProtection:KeyDirectory"] = Path.Combine(Path.GetTempPath(), "WorkerDock-Keys-" + Guid.NewGuid().ToString("N")) })));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("""{"status":"live"}""", await live.Content.ReadAsStringAsync());
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var readiness = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.NotEqual("Healthy", readiness.RootElement.GetProperty("overall").GetString());
        Assert.True(readiness.RootElement.TryGetProperty("instanceId", out _));
        Assert.True(readiness.RootElement.TryGetProperty("components", out _));
        Assert.True(readiness.RootElement.TryGetProperty("observedUtc", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_test/settings")).StatusCode);
    }
}

public sealed record SettingsResponse(string DisplayName, string DisplayTimeZone, string Version);
