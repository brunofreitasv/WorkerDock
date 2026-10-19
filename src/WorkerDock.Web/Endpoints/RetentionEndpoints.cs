using System.Text.Json.Serialization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Retention;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetentionPolicyUpdate(int LogDays, int ExecutionDays, int AuditDays, bool ArchiveBeforeDelete, int ArchiveDays);

public static class RetentionEndpoints
{
    public static void MapRetention(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/settings/retention").RequireAuthorization(AccessPolicies.Manage);
        group.MapGet("", async (HttpResponse response, IRetentionService service, CancellationToken ct) =>
        {
            try
            {
                var value = await service.GetPolicyAsync(ct);
                response.Headers.ETag = Tag(value.Version);
                return Results.Ok(new { value.Policy.LogDays, value.Policy.ExecutionDays, value.Policy.AuditDays,
                    value.Policy.ArchiveBeforeDelete, value.Policy.ArchiveDays, Version = Convert.ToBase64String(value.Version) });
            }
            catch (WorkerDockException error) { return Error(error); }
        });
        group.MapPut("", async (HttpContext context, RetentionPolicyUpdate input, IRetentionService service, CancellationToken ct) =>
        {
            byte[] expected;
            try { expected = Version(context); }
            catch (WorkerDockException error) { return Error(error); }
            try
            {
                var policy = new RetentionPolicy(input.LogDays, input.ExecutionDays, input.AuditDays, input.ArchiveBeforeDelete, input.ArchiveDays);
                var receipt = await service.SavePolicyAsync(policy, expected, SessionEndpoints.Actor(context), ct);
                var saved = await service.GetPolicyAsync(ct);
                context.Response.Headers.ETag = Tag(saved.Version);
                return Results.Ok(new { saved.Policy.LogDays, saved.Policy.ExecutionDays, saved.Policy.AuditDays,
                    saved.Policy.ArchiveBeforeDelete, saved.Policy.ArchiveDays, Version = Convert.ToBase64String(saved.Version),
                    commandId = receipt.CommandId });
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
        "ValidationFailed" => 400,
        "PersistenceUnavailable" or "TenantInstanceMismatch" => 503,
        _ => 400
    }, extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["field"] = error.Field ?? "" });
}
