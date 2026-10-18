using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Authentication;
namespace WorkerDock.Web.Endpoints;

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
        }).RequireAuthorization(AccessPolicies.Manage);
        routes.MapGet("/api/templates/{id}/{version}",async (string id,string version,ITemplateCatalog catalog,CancellationToken ct)=>
        {
            var descriptor=await catalog.GetAsync(id,version,ct);
            return descriptor is null ? Results.NotFound() : Results.Ok(new { descriptor,selectable=await catalog.IsSelectableAsync(id,version,ct) });
        }).RequireAuthorization(AccessPolicies.Manage);
    }
}
