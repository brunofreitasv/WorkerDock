using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Templates;
namespace WorkerDock.IntegrationTests;
public sealed partial class TemplateCatalogTests
{
    [Fact]
    public async Task Forms_receive_only_registered_metadata_and_viewer_cannot_read_catalog()
    {
        var descriptor=Descriptor(); var catalog=new TemplateCatalog([descriptor]); await catalog.RegisterAsync([descriptor],default);
        await using var baseHost=new AccessHost(); await using var host=baseHost.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<ITemplateCatalog>(catalog))); using var client=host.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
        await client.GetAsync("/api/session");
        using var response=await client.GetAsync("/api/templates"); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync(); Assert.Contains(descriptor.MetadataDigest,text); Assert.DoesNotContain("password",text,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/templates/not.installed/1" )).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/secrets/resolve")).StatusCode);
        using var issuer=new LocalOidcIssuer("Viewer"); await using var protectedHost=new AccessHost(issuer,"Manager"); using var viewer=protectedHost.Client(); await issuer.SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync("/api/templates")).StatusCode);
    }
}
