using System.Net;
using System.Net.Http.Json;
namespace WorkerDock.IntegrationTests;
public sealed class CapacityEndpointTests
{
    [Fact]
    public async Task Viewer_cannot_change_capacity_and_mutations_require_csrf()
    {
        using var issuer=new LocalOidcIssuer("Viewer");
        await using var host=new AccessHost(issuer,"Manager"); using var client=host.Client(); await issuer.SignIn(client);
        var token=(await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        foreach(var route in new[] { "/api/templates/workerdock.http/limit",$"/api/jobs/{Guid.NewGuid()}/limit",$"/api/workers/{Guid.NewGuid()}/limit" })
        {
            using var request=new HttpRequestMessage(HttpMethod.Put,route) { Content=JsonContent.Create(new { limit=1 }) };
            request.Headers.Add("Origin","https://localhost"); request.Headers.Add("X-WorkerDock-CSRF",token);
            using var response=await client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        }
        await using var anonymous=new AccessHost(); using var browser=anonymous.Client(); await browser.GetAsync("/api/session");
        using var noCsrf=await browser.PutAsJsonAsync("/api/templates/workerdock.http/limit",new { limit=1 });
        Assert.Equal(HttpStatusCode.BadRequest,noCsrf.StatusCode);
    }
}
