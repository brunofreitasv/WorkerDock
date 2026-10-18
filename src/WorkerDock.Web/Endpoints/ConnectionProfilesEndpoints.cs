using System.Text.Json.Serialization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Connections;
using WorkerDock.Contracts.Connections;
using WorkerDock.Infrastructure.Authentication;
namespace WorkerDock.Web.Endpoints;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileDiagnosticRequest(string Catalog);
public static class ConnectionProfilesEndpoints
{
    public static void MapConnectionProfiles(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/connection-profiles").RequireAuthorization(AccessPolicies.Manage);
        group.MapGet("/",async(IConnectionProfiles profiles,CancellationToken ct)=>
        { try{return Results.Ok(new { items=await profiles.ListAsync(ct) });}catch(WorkerDockException e){return Error(e);} });
        group.MapGet("/{id:guid}",async(Guid id,IConnectionProfiles profiles,HttpResponse response,CancellationToken ct)=>
        { try{var result=await profiles.GetCurrentAsync(id,ct);response.Headers.ETag=Tag(result.Version);return Results.Ok(result);}catch(WorkerDockException e){return Error(e);} });
        group.MapPost("/",async(ConnectionProfileRevision profile,IConnectionProfiles profiles,HttpContext context,CancellationToken ct)=>
        {
            if(profile.ProfileId!=Guid.Empty || profile.ProfileRevisionId!=Guid.Empty || profile.Revision!=0)return Error(new("ValidationFailed","profile"));
            try{var id=Guid.NewGuid();var result=await profiles.SaveAsync(profile with { ProfileId=id },null,SessionEndpoints.Actor(context),ct);return Results.Accepted($"/api/connection-profiles/{id}",result);}catch(WorkerDockException e){return Error(e);}
        });
        group.MapPut("/{id:guid}",async(Guid id,ConnectionProfileRevision profile,IConnectionProfiles profiles,HttpContext context,CancellationToken ct)=>
        {
            if(profile.ProfileId!=id)return Error(new("ValidationFailed","profileId"));
            try{return Results.Accepted(value:await profiles.SaveAsync(profile,Version(context),SessionEndpoints.Actor(context),ct));}catch(WorkerDockException e){return Error(e);}
        });
        group.MapPost("/{id:guid}/retire",async(Guid id,IConnectionProfiles profiles,HttpContext context,CancellationToken ct)=>
        { try{return Results.Accepted(value:await profiles.RetireAsync(id,Version(context),SessionEndpoints.Actor(context),ct));}catch(WorkerDockException e){return Error(e);} });
        group.MapPost("/{id:guid}/diagnostics",async(Guid id,ProfileDiagnosticRequest request,IProfileDiagnostics diagnostics,HttpContext context,CancellationToken ct)=>
        { try{var result=await diagnostics.StartAsync(id,request.Catalog,SessionEndpoints.Actor(context),ct);return Results.Accepted($"/api/commands/{result.CommandId}",result);}catch(WorkerDockException e){return Error(e);} });
    }
    private static string Tag(byte[] version)=>'"'+Convert.ToBase64String(version)+'"';
    private static byte[] Version(HttpContext context)
    {
        var tag=context.Request.Headers.IfMatch.ToString();
        if(tag.Length<3 || tag[0]!='"' || tag[^1]!='"')throw new WorkerDockException("StaleVersion");
        try{var value=Convert.FromBase64String(tag[1..^1]);return value.Length==8 ? value : throw new WorkerDockException("StaleVersion");}catch(FormatException){throw new WorkerDockException("StaleVersion");}
    }
    private static IResult Error(WorkerDockException e)=>Results.Problem(statusCode:e.Code switch
    { "StaleVersion"=>412,"ValidationFailed" or "CatalogNotAllowed"=>400,"ProfileNotFound"=>404,"ProfileRetired" or "ProfileNameConflict" or "ProfileLimitReached" or "DiagnosticLimitReached" or "ProfileUnavailable" or "TemplateVersionUnavailable"=>409,_=>503 },extensions:new Dictionary<string,object?>{["code"]=e.Code,["field"]=e.Field ?? ""});
}
