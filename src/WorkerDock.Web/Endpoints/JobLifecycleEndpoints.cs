using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

public sealed record JobLifecycleRequest(string Action, Guid? TargetJobId = null);

public static class JobLifecycleEndpoints
{
    public static void MapJobLifecycle(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs/{id:guid}/actions", async (Guid id, JobLifecycleRequest request, IJobLifecycle lifecycle, IJobDuplicator duplicator, HttpRequest http, CancellationToken ct) =>
        {
            var tag = http.Headers.IfMatch.ToString();
            if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"') return Problem(412, "InvalidIfMatch", "headers.ifMatch");
            byte[] version;
            try { version = Convert.FromBase64String(tag[1..^1]); if (version.Length != 8) return Problem(412, "InvalidIfMatch", "headers.ifMatch"); }
            catch (FormatException) { return Problem(412, "InvalidIfMatch", "headers.ifMatch"); }
            try
            {
                var actor = SessionEndpoints.Actor(http.HttpContext);
                var receipt = request.Action == "Duplicate"
                    ? await duplicator.DuplicateAsync(id, request.TargetJobId ?? Guid.Empty, version, actor, ct)
                    : await lifecycle.ApplyAsync(id, request.Action, version, actor, ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException error) { return error.Code switch
            {
                "ValidationFailed" => Problem(400, error.Code, error.Field),
                "JobNotFound" or "ScheduleNotFound" => Problem(404, error.Code),
                "StaleVersion" => Problem(412, error.Code, "headers.ifMatch"),
                "JobArchived" or "JobDisabled" or "ProfileUnavailable" or "ScheduleExists" => Problem(409, error.Code),
                _ => Problem(503, "PersistenceUnavailable")
            }; }
        }).RequireAuthorization(AccessPolicies.Manage);
    }
    private static IResult Problem(int status,string code,string? field=null)=>Results.Problem(statusCode:status,extensions:new Dictionary<string,object?>{{"code",code},{"field",field??""}});
}
