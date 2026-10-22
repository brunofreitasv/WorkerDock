using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Executions;

namespace WorkerDock.IntegrationTests;

public sealed class ExecutionControlsEndpointTests
{
    [Fact]
    public async Task Production_memory_selection_never_falls_back_to_SQL_controls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkerDockControlKeys_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var host = new AccessHost();
            await using var configured = host.WithWebHostBuilder(builder => builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Tenant:Adapter"] = "Memory", ["DataProtection:KeyDirectory"] = directory })));
            using var client = configured.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var token = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
            using var response = await Send(client, token, $"/api/executions/{Guid.NewGuid()}/stop", new { force = false, confirmForce = false, reason = "request" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [Fact]
    public async Task Force_requires_confirmation_and_reason_then_acknowledges_persisted_stop()
    {
        var descriptor = AttemptLifecycleTests.Descriptor();
        var store = new InMemoryAttemptStore(TimeProvider.System, descriptor, AttemptLifecycleTests.Policy());
        await using var host = new AccessHost();
        await using var configured = host.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IExecutionControls>(store)));
        using var client = configured.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var token = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        var path = $"/api/executions/{descriptor.ExecutionId}/stop";
        using var missing = await Send(client, token, path, new { force = true, confirmForce = false, reason = "operator" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.False(store.Snapshot.StopRequested);
        using var blank = await Send(client, token, path, new { force = true, confirmForce = true, reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        using var accepted = await Send(client, token, path, new { force = true, confirmForce = true, reason = "operator confirmed" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(store.Snapshot.StopRequested);
        Assert.Equal("Stopped", store.Snapshot.State);
        Assert.Single(store.Audits);
    }

    [Fact]
    public async Task Viewer_cannot_stop_or_resolve_and_csrf_is_required()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var host = new AccessHost(issuer, "Manager"); using var client = host.Client();
        await issuer.SignIn(client);
        var token = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        foreach (var action in new[] { "stop", "resolve" })
        {
            using var denied = await Send(client, token, $"/api/executions/{Guid.NewGuid()}/{action}", new { force = false, reason = "request", decision = "Cancel" });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await using var anonymous = new AccessHost(); using var browser = anonymous.Client();
        await browser.GetAsync("/api/session");
        using var noCsrf = await browser.PostAsJsonAsync($"/api/executions/{Guid.NewGuid()}/stop", new { force = false, reason = "request" });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string token, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "https://localhost"); request.Headers.Add("X-WorkerDock-CSRF", token);
        return client.SendAsync(request);
    }
}
