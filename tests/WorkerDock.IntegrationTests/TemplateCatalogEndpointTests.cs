using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Templates.SqlServer;
using WorkerDock.Templates.Http;
namespace WorkerDock.IntegrationTests;
public sealed partial class TemplateCatalogTests
{
    [Fact]
    public async Task Removed_builtin_versions_are_absent_from_catalog_and_direct_lookup()
    {
        var descriptors = new WorkerDock.Contracts.Templates.TemplateDescriptor[]
        {
            SqlTemplateDescriptors.Get("1.1.0"), HttpTemplateDescriptors.Get("1.1.0")
        };
        var catalog = new TemplateCatalog(descriptors); await catalog.RegisterAsync(descriptors, default);
        await using var baseHost = new AccessHost();
        await using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ITemplateCatalog>(catalog)));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        using var list = await client.GetAsync("/api/templates");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await list.Content.ReadAsStringAsync();
        Assert.Contains("1.1.0", body); Assert.DoesNotContain("1.0.0", body);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/templates/workerdock.http/1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/templates/workerdock.sql-server/1.0.0")).StatusCode);
    }
    [Fact]
    public async Task Template_validation_uses_sql_semantic_authoring_rules()
    {
        var descriptor = new SqlTemplate("1.1.0").Descriptor;
        var catalog = new TemplateCatalog([descriptor]);
        await catalog.RegisterAsync([descriptor], default);
        using var issuer = new LocalOidcIssuer("Manager");
        await using var baseHost = new AccessHost(issuer, "Manager");
        await using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ITemplateCatalog>(catalog)));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await issuer.SignIn(client);
        var token = await client.GetFromJsonAsync<JsonElement>("/api/antiforgery");
        client.DefaultRequestHeaders.Add("X-WorkerDock-CSRF", token.GetProperty("requestToken").GetString());
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var configuration = new { profileId = Guid.NewGuid(), catalog = "jobs", commandKind = "Procedure", commandText = "EXEC dbo.Job @Dt_Ref = @Dt_Ref", parameters = Array.Empty<object>(), commandTimeoutSeconds = (int?)null, useTransaction = false };

        using var procedure = await client.PostAsJsonAsync("/api/template-validation", new { templateId = descriptor.Id, templateVersion = descriptor.Version, configuration });
        var procedureIssues = await procedure.Content.ReadAsStringAsync();
        Assert.Contains("SqlProcedureNameRequired", procedureIssues);
        Assert.DoesNotContain("dbo.Job", procedureIssues);

        using var timeout = await client.PostAsJsonAsync("/api/template-validation", new { templateId = descriptor.Id, templateVersion = descriptor.Version, configuration = new { configuration.profileId, configuration.catalog, commandKind = "Query", commandText = "SELECT 1", parameters = Array.Empty<object>(), commandTimeoutSeconds = 0, useTransaction = false }, jobTimeout = "00:00:30" });
        Assert.Contains("CommandTimeoutInvalid", await timeout.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Catalog_is_readable_by_viewers_but_validation_requires_manage_and_never_echoes_configuration()
    {
        var descriptor=Descriptor(); var catalog=new TemplateCatalog([descriptor]); await catalog.RegisterAsync([descriptor],default);
        await using var baseHost=new AccessHost(); await using var host=baseHost.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<ITemplateCatalog>(catalog))); using var client=host.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
        await client.GetAsync("/api/session");
        using var response=await client.GetAsync("/api/templates"); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync(); Assert.Contains(descriptor.MetadataDigest,text); Assert.DoesNotContain("password",text,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/templates/not.installed/1" )).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/secrets/resolve")).StatusCode);
        using var issuer=new LocalOidcIssuer("Viewer"); await using var protectedBase=new AccessHost(issuer,"Manager"); await using var protectedHost=protectedBase.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<ITemplateCatalog>(catalog))); using var viewer=protectedHost.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false }); await issuer.SignIn(viewer);
        using var viewerCatalog=await viewer.GetAsync("/api/templates"); Assert.True(viewerCatalog.StatusCode==HttpStatusCode.OK,await viewerCatalog.Content.ReadAsStringAsync());
        var viewerToken=await viewer.GetFromJsonAsync<JsonElement>("/api/antiforgery"); viewer.DefaultRequestHeaders.Add("X-WorkerDock-CSRF",viewerToken.GetProperty("requestToken").GetString()); viewer.DefaultRequestHeaders.Add("Origin","https://localhost");
        var denied=await viewer.PostAsJsonAsync("/api/template-validation",new { templateId=descriptor.Id,templateVersion=descriptor.Version,configuration=new { password="sentinel" } });
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using var managerIssuer=new LocalOidcIssuer("Manager"); await using var managerBase=new AccessHost(managerIssuer,"Manager"); await using var managerHost=managerBase.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<ITemplateCatalog>(catalog))); using var manager=managerHost.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false }); await managerIssuer.SignIn(manager);
        var token=await manager.GetFromJsonAsync<JsonElement>("/api/antiforgery");
        manager.DefaultRequestHeaders.Add("X-WorkerDock-CSRF",token.GetProperty("requestToken").GetString()); manager.DefaultRequestHeaders.Add("Origin","https://localhost");
        using var invalid=await manager.PostAsJsonAsync("/api/template-validation",new { templateId=descriptor.Id,templateVersion=descriptor.Version,configuration=new { password="sentinel" } });
        Assert.Equal(HttpStatusCode.OK,invalid.StatusCode);
        var issues=await invalid.Content.ReadAsStringAsync(); Assert.Contains("AdditionalProperty",issues); Assert.DoesNotContain("sentinel",issues,StringComparison.OrdinalIgnoreCase);
    }
}
