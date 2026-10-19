using System.Net;
using System.Net.Http.Json;

namespace WorkerDock.IntegrationTests;

public sealed class AlertAccessTests
{
    [Fact]
    public async Task Authenticated_viewer_can_read_alert_monitoring_but_cannot_manage_alert_rules_or_retry()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var host = new AccessHost(issuer, "AlertAdmin");
        using var client = host.Client();
        await issuer.SignIn(client);
        var csrf = await client.GetFromJsonAsync<CsrfView>("/api/antiforgery");

        using var incidents = await client.GetAsync("/api/alerts/incidents");
        using var rules = await client.GetAsync("/api/alert-rules");
        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/alerts/deliveries/{Guid.NewGuid()}/retry")
        { Content = JsonContent.Create(new { additionalAttempts = 1 }) };
        retryRequest.Headers.Add("Origin", "https://localhost");
        retryRequest.Headers.Add("X-WorkerDock-CSRF", csrf!.RequestToken);
        using var retry = await client.SendAsync(retryRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, incidents.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rules.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
    }

    [Fact]
    public async Task Configured_manager_can_read_alert_rule_management()
    {
        using var issuer = new LocalOidcIssuer("AlertAdmin");
        await using var host = new AccessHost(issuer, "AlertAdmin");
        using var client = host.Client();
        await issuer.SignIn(client);

        using var rules = await client.GetAsync("/api/alert-rules");
        using var deliveries = await client.GetAsync("/api/alerts/deliveries");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rules.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, deliveries.StatusCode);
    }
}
