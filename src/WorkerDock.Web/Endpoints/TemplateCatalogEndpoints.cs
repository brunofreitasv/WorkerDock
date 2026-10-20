using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Contracts.Values;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Web.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateValidationRequest(string TemplateId, string TemplateVersion, JsonElement Configuration,
    JsonElement? DynamicValues = null, TimeSpan? JobTimeout = null, string? TriggerKind = null);

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
        routes.MapPost("/api/template-validation", async (TemplateValidationRequest request, ITemplateCatalog catalog, ITemplateAuthoringService authoring, CancellationToken ct) =>
        {
            if (request.Configuration.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { issues = new[] { new { path = "$", code = "Type" } } });
            var descriptor = await catalog.GetAsync(request.TemplateId, request.TemplateVersion, ct);
            if (descriptor is null || !await catalog.IsSelectableAsync(request.TemplateId, request.TemplateVersion, ct))
                return Results.Ok(new { issues = new[] { new { path = "$", code = "TemplateUnavailable" } } });
            DynamicValuePlan? plan = null;
            if (request.DynamicValues is { ValueKind: not JsonValueKind.Null } dynamicValues)
            {
                try { plan = DynamicValueJson.Read(dynamicValues); }
                catch (ValueResolutionException error) { return Results.Ok(new { issues = new[] { new { path = "configuration.dynamicValues", code = error.Code } } }); }
            }
            if (request.TriggerKind is not (null or "Scheduled" or "Manual"))
                return Results.BadRequest(new { issues = new[] { new { path = "triggerKind", code = "Value" } } });
            var issues = authoring.Validate(new TemplateAuthoringContext(descriptor, request.Configuration, plan, request.JobTimeout, request.TriggerKind));
            return Results.Ok(new { issues });
        }).RequireAuthorization(AccessPolicies.Manage);
    }
}
