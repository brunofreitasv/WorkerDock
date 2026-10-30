using System.Text.Json.Serialization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Monitoring;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Jobs;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

public sealed record SaveJobDefinitionRequest(JobRevision Revision, [property: JsonRequired] ScheduleEditorInput? Schedule, [property: JsonRequired] MissedWorkPolicy? MissedPolicy);

public static class JobManagementEndpoints
{
    public static void MapJobManagement(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", async (IJobManagementQueries queries, HttpRequest request, CancellationToken ct) =>
        {
            try
            {
                var filter = new JobListFilter(request.Query["view"].FirstOrDefault() ?? "current", request.Query["search"].FirstOrDefault());
                return Results.Ok(await queries.ListAsync(filter, Page(request), ct));
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Read);

        app.MapGet("/api/jobs/{id:guid}/execution-controls", async (Guid id, IJobManagementQueries queries, CancellationToken ct) =>
        {
            try
            {
                var detail = await queries.GetAsync(id, ct);
                return Results.Ok(new { limit = detail.Editor.ConcurrencyLimit, pauseOnNeedsReview = detail.Editor.PauseOnNeedsReview });
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Read);

        app.MapGet("/api/jobs/{id:guid}", async (Guid id, IJobManagementQueries queries, HttpResponse response, CancellationToken ct) =>
        {
            try
            {
                var detail = await queries.GetAsync(id, ct);
                response.Headers.ETag = '"' + Convert.ToBase64String(detail.Editor.Version) + '"';
                return Results.Ok(detail);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapGet("/api/jobs/{id:guid}/executions", async (Guid id, IJobManagementQueries queries, HttpRequest request, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.ExecutionsAsync(id, Page(request), ct)); }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapPut("/api/jobs/{id:guid}/definition", async (Guid id, SaveJobDefinitionRequest input, IJobDefinitionCommands commands, HttpRequest request, CancellationToken ct) =>
        {
            if (!TryExpectedVersion(request, out var version)) return Problem(412, "InvalidIfMatch", "headers.ifMatch");
            if (input.Revision is null) return Problem(400, "ValidationFailed", "revision");
            try
            {
                var receipt = await commands.SaveDefinitionAsync(id, input.Revision, input.Schedule, input.MissedPolicy, version, SessionEndpoints.Actor(request.HttpContext), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);
    }

    private static PageRequest Page(HttpRequest request)
    {
        var value = request.Query["limit"].FirstOrDefault();
        if (value is not null && (!int.TryParse(value, out var parsed) || parsed is < 1 or > 100)) throw new WorkerDockException("ValidationFailed", "limit");
        return new(request.Query["cursor"].FirstOrDefault(), value is null ? 50 : int.Parse(value));
    }
    private static bool TryExpectedVersion(HttpRequest request, out byte[] version)
    {
        version = []; var tag = request.Headers.IfMatch.ToString();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"') return false;
        try { version = Convert.FromBase64String(tag[1..^1]); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
    private static IResult Error(WorkerDockException error) => error.Code switch
    {
        "ValidationFailed" => Problem(400, error.Code, error.Field),
        "JobNotFound" => Problem(404, error.Code),
        "StaleVersion" => Problem(412, error.Code, "headers.ifMatch"),
        "JobArchived" or "ProfileUnavailable" or "UnknownTemplate" => Problem(409, error.Code, error.Field),
        _ => Problem(503, "PersistenceUnavailable")
    };
    private static IResult Problem(int status, string code, string? field = null) => Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code, ["field"] = field ?? "" });
}
