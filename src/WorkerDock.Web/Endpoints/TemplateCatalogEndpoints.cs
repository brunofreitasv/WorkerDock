using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkerDock.Infrastructure.Templates;
namespace WorkerDock.Web.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateValidationRequest(string TemplateId, string TemplateVersion, JsonElement Configuration);

public static class TemplateCatalogEndpoints
{
    public static void MapTemplateCatalog(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/templates",async (ITemplateCatalog catalog,CancellationToken ct)=>
        {
            var descriptors=await catalog.ListAsync(ct);
            var items=new List<object>();
            foreach(var descriptor in descriptors)
                items.Add(new { descriptor, selectable=await catalog.IsSelectableAsync(descriptor.Id,descriptor.Version,ct) });
            return Results.Ok(new { items });
        }).RequireAuthorization(AccessPolicies.Read);
        routes.MapGet("/api/templates/{id}/{version}",async (string id,string version,ITemplateCatalog catalog,CancellationToken ct)=>
        {
            var descriptor=await catalog.GetAsync(id,version,ct);
            return descriptor is null ? Results.NotFound() : Results.Ok(new { descriptor,selectable=await catalog.IsSelectableAsync(id,version,ct) });
        }).RequireAuthorization(AccessPolicies.Read);
        routes.MapPost("/api/template-validation", async (TemplateValidationRequest request, ITemplateCatalog catalog, CancellationToken ct) =>
        {
            if (request.Configuration.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { issues = new[] { new { path = "$", code = "Type" } } });
            var descriptor = await catalog.GetAsync(request.TemplateId, request.TemplateVersion, ct);
            if (descriptor is null || !await catalog.IsSelectableAsync(request.TemplateId, request.TemplateVersion, ct))
                return Results.Ok(new { issues = new[] { new { path = "$", code = "TemplateUnavailable" } } });
            var issues = TemplateSchemaValidator.Validate(descriptor, request.Configuration);
            return Results.Ok(new { issues });
        }).RequireAuthorization(AccessPolicies.Manage);
    }
}
