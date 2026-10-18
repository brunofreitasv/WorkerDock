using System.Text.Json.Serialization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Settings;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VaultSettingsUpdate(string ProviderId, System.Text.Json.JsonElement PublicOptions);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EgressSettingsUpdate(IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports, IReadOnlyList<string> PrivateCidrs, bool AllowHttp);

public static class IntegrationSettingsEndpoints
{
    public static void MapIntegrationSettings(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/settings").RequireAuthorization(AccessPolicies.Manage);
        group.MapGet("/vault", async (IIntegrationSettings settings, HttpResponse response, CancellationToken ct) =>
        {
            try { var value = await settings.GetVaultAsync(ct); response.Headers.ETag = Tag(value.Version); return Results.Ok(new { value.ProviderId, value.PublicOptions, Version = Convert.ToBase64String(value.Version) }); }
            catch (WorkerDockException error) { return Error(error); }
        });
        group.MapPut("/vault", async (HttpContext context, VaultSettingsUpdate input, IIntegrationSettings settings, CancellationToken ct) =>
        {
            try
            {
                var current = await settings.GetVaultAsync(ct);
                var saved = await settings.SaveVaultAsync(new(input.ProviderId, input.PublicOptions, Version(context)), SessionEndpoints.Actor(context), ct);
                var updated = await settings.GetVaultAsync(ct); context.Response.Headers.ETag = Tag(updated.Version);
                return Results.Ok(new { updated.ProviderId, updated.PublicOptions, Version = Convert.ToBase64String(updated.Version), commandId = saved.CommandId });
            }
            catch (WorkerDockException error) { return Error(error); }
        });
        group.MapGet("/egress/{name}", async (string name, IIntegrationSettings settings, HttpResponse response, CancellationToken ct) =>
        {
            try { var value = await settings.GetEgressAsync(name, ct); response.Headers.ETag = Tag(value.Version); return Results.Ok(new { value.Name, value.Hosts, value.Ports, value.PrivateCidrs, value.AllowHttp, Version = Convert.ToBase64String(value.Version) }); }
            catch (WorkerDockException error) { return Error(error); }
        });
        group.MapPut("/egress/{name}", async (string name, HttpContext context, EgressSettingsUpdate input, IIntegrationSettings settings, CancellationToken ct) =>
        {
            try
            {
                var current = await settings.GetEgressAsync(name, ct);
                var saved = await settings.SaveEgressAsync(new(name, input.Hosts, input.Ports, input.PrivateCidrs, input.AllowHttp, Version(context)), SessionEndpoints.Actor(context), ct);
                var updated = await settings.GetEgressAsync(name, ct); context.Response.Headers.ETag = Tag(updated.Version);
                return Results.Ok(new { updated.Name, updated.Hosts, updated.Ports, updated.PrivateCidrs, updated.AllowHttp, Version = Convert.ToBase64String(updated.Version), commandId = saved.CommandId });
            }
            catch (WorkerDockException error) { return Error(error); }
        });
    }

    private static byte[] Version(HttpContext context)
    {
        var tag = context.Request.Headers.IfMatch.ToString();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"') throw new WorkerDockException("StaleVersion");
        try { var version = Convert.FromBase64String(tag[1..^1]); return version.Length == 8 ? version : throw new WorkerDockException("StaleVersion"); }
        catch (FormatException) { throw new WorkerDockException("StaleVersion"); }
    }

    private static string Tag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';
    private static IResult Error(WorkerDockException error) => Results.Problem(statusCode: error.Code switch
    {
        "StaleVersion" => 412,
        "ValidationFailed" or "DeploymentPolicyViolation" => 400,
        "ProviderNotInstalled" => 409,
        "EgressPolicyNotFound" => 404,
        "TenantInstanceMismatch" or "PersistenceUnavailable" or "SettingsUnavailable" => 503,
        _ => 400
    }, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["field"] = error.Field ?? "" });
}
