using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.IntegrationTests;

public sealed class DynamicValuePreviewEndpointTests
{
    [Fact]
    public async Task Preview_uses_shared_resolver_and_returns_only_example_bound_values()
    {
        var descriptor = DynamicDescriptor();
        var catalog = new TemplateCatalog([descriptor]);
        await catalog.RegisterAsync([descriptor], default);
        using var issuer = new LocalOidcIssuer("Manager");
        await using var baseHost = new AccessHost(issuer, "Manager");
        await using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ITemplateCatalog>(catalog)));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await issuer.SignIn(client);
        AddCsrf(client, await client.GetFromJsonAsync<JsonElement>("/api/antiforgery"));

        using var response = await client.PostAsJsonAsync("/api/template-values/preview", new
        {
            templateId = descriptor.Id,
            templateVersion = descriptor.Version,
            configuration = new { parameters = new { } },
            dynamicValues = new
            {
                resolverVersion = 1,
                timeZoneId = "UTC",
                bindings = new[] { new { target = "/parameters/referenceDate", source = "ReferenceDate", timeZoneId = (string?)null, months = 0, days = -1, boundary = "None", format = "IsoDate" } }
            },
            triggerKind = "Scheduled",
            sampleAtUtc = DateTimeOffset.Parse("2026-10-21T12:00:00Z"),
            scheduledForUtc = DateTimeOffset.Parse("2026-10-20T09:00:00Z"),
            referenceDateOverride = (string?)null,
            jobTimeout = (string?)null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("issues").EnumerateArray());
        Assert.True(body.RootElement.GetProperty("isExample").GetBoolean());
        Assert.Equal("2026-10-19", body.RootElement.GetProperty("values").GetProperty("/parameters/referenceDate").GetString());
        Assert.Single(body.RootElement.GetProperty("values").EnumerateObject());
    }

    [Fact]
    public async Task Preview_requires_manage_role_and_antiforgery()
    {
        var descriptor = DynamicDescriptor();
        var catalog = new TemplateCatalog([descriptor]);
        await catalog.RegisterAsync([descriptor], default);
        using var viewerIssuer = new LocalOidcIssuer("Viewer");
        await using var viewerBase = new AccessHost(viewerIssuer, "Manager");
        await using var viewerHost = viewerBase.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ITemplateCatalog>(catalog)));
        using var viewer = viewerHost.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await viewerIssuer.SignIn(viewer);
        var payload = new { templateId = descriptor.Id, templateVersion = descriptor.Version, configuration = new { parameters = new { } }, dynamicValues = new { resolverVersion = 1, timeZoneId = "UTC", bindings = Array.Empty<object>() }, triggerKind = "Manual", sampleAtUtc = DateTimeOffset.Parse("2026-10-20T12:00:00Z"), scheduledForUtc = (string?)null, referenceDateOverride = (string?)null, jobTimeout = (string?)null };
        AddCsrf(viewer, await viewer.GetFromJsonAsync<JsonElement>("/api/antiforgery"));
        using var denied = await viewer.PostAsJsonAsync("/api/template-values/preview", payload);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var anonymousHost = new AccessHost();
        using var anonymous = anonymousHost.Client();
        await anonymous.GetAsync("/api/session");
        using var noToken = await anonymous.PostAsJsonAsync("/api/template-values/preview", payload);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
    }

    private static TemplateDescriptor DynamicDescriptor()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["parameters"],"properties":{"parameters":{"type":"object","maxProperties":16,"additionalProperties":{"type":"string","x-workerDock-dynamicValue":true}}}}""");
        return TemplateDescriptorMetadata.WithDigest(new("test.dynamic-preview", "1.0.0", "Preview fixture", "Test only", schema.RootElement.Clone(), JsonSerializer.SerializeToElement(new { }), ""));
    }

    private static void AddCsrf(HttpClient client, JsonElement token)
    {
        client.DefaultRequestHeaders.Add("X-WorkerDock-CSRF", token.GetProperty("requestToken").GetString());
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
    }
}
