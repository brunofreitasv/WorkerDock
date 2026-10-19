using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json.Serialization;
using WorkerDock.Application.Common;
using WorkerDock.ControlCenter.Health;
using WorkerDock.ControlCenter.Registry;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.ControlCenter.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantRegistrationRequest(string DisplayName, Guid ExpectedInstanceId, string DashboardUrl, string HealthCredentialReference);

public static class RegistryEndpoints
{
    public static void MapRegistry(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/tenants");
        group.MapGet("", async (string? search, string? cursor, int? limit, ICentralRegistry registry, CancellationToken ct) =>
        {
            try { return Results.Ok(await registry.ListAsync(search, new(cursor, limit ?? 50), ct)); }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Read);

        group.MapPost("", async (HttpContext context, TenantRegistrationRequest request, ICentralRegistry registry, CancellationToken ct) =>
        {
            try
            {
                var entry = await registry.SaveAsync(null, new(request.DisplayName, request.ExpectedInstanceId, request.DashboardUrl, request.HealthCredentialReference), null,
                    SessionEndpoints.Actor(context), ct);
                context.Response.Headers.ETag = Tag(entry.Version);
                return Results.Created($"/api/tenants/{entry.Id}", entry);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        group.MapPut("/{id:guid}", async (Guid id, HttpContext context, TenantRegistrationRequest request, ICentralRegistry registry, CancellationToken ct) =>
        {
            try
            {
                var entry = await registry.SaveAsync(id, new(request.DisplayName, request.ExpectedInstanceId, request.DashboardUrl, request.HealthCredentialReference),
                    Version(context), SessionEndpoints.Actor(context), ct);
                context.Response.Headers.ETag = Tag(entry.Version);
                return Results.Ok(entry);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext context, ICentralRegistry registry, CancellationToken ct) =>
        {
            try
            {
                await registry.RemoveAsync(id, Version(context), SessionEndpoints.Actor(context), ct);
                return Results.NoContent();
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        group.MapPost("/{id:guid}/health/refresh", async (Guid id, ITenantHealthProbe probe, CancellationToken ct) =>
        {
            try { return Results.Ok(await probe.CheckAsync(id, ct)); }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Read).RequireRateLimiting("central-health-refresh");
    }

    private static byte[] Version(HttpContext context)
    {
        var tag = context.Request.Headers.IfMatch.ToString();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"') throw new WorkerDockException("StaleVersion", "headers.ifMatch");
        try { var version = Convert.FromBase64String(tag[1..^1]); return version.Length == 8 ? version : throw new WorkerDockException("StaleVersion", "headers.ifMatch"); }
        catch (FormatException) { throw new WorkerDockException("StaleVersion", "headers.ifMatch"); }
    }
    private static string Tag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';
    private static IResult Error(WorkerDockException error) => Results.Problem(statusCode: error.Code switch
    {
        "StaleVersion" => 412,
        "ValidationFailed" => 400,
        "TenantRegistrationNotFound" => 404,
        "PersistenceUnavailable" or "CentralInstanceMismatch" => 503,
        _ => 400
    }, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["field"] = error.Field ?? "" });
}
