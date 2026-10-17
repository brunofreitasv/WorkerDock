using System.Net;
using System.Net.Http.Json;
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
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tenant:Adapter"] = "Memory", ["Tenant:InstanceId"] = instanceId.ToString(),
                    ["Tenant:ConnectionString"] = ""
                })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var original = await client.GetFromJsonAsync<SettingsResponse>("/_test/settings");
        Assert.NotNull(original);
        using var update = new HttpRequestMessage(HttpMethod.Put, "/_test/settings")
        {
            Content = JsonContent.Create(new { displayName = "Equipe", displayTimeZone = "America/Sao_Paulo" })
        };
        update.Headers.TryAddWithoutValidation("If-Match", '"' + original.Version + '"');
        update.Headers.TryAddWithoutValidation("X-WorkerDock-CSRF", "development");
        using var response = await client.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = await client.GetFromJsonAsync<SettingsResponse>("/_test/settings");
        Assert.Equal("Equipe", changed?.DisplayName);
        Assert.NotEqual(original.Version, changed?.Version);
    }

    [Fact]
    public async Task Liveness_is_safe_and_readiness_fails_closed_without_database_configuration()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Tenant:ConnectionString"] = "", ["Tenant:InstanceId"] = "" })));
        using var client = factory.CreateClient();
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("""{"status":"live"}""", await live.Content.ReadAsStringAsync());
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"unavailable"}""", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_test/settings")).StatusCode);
    }
}

public sealed record SettingsResponse(string DisplayName, string DisplayTimeZone, string Version);
