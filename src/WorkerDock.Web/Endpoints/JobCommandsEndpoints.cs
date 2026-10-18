using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

public sealed record RunNowRequest(string RequestKey);

public static class JobCommandsEndpoints
{
    public static void MapJobCommands(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs", async (JobRevision proposed, IJobCommands commands, HttpContext context, CancellationToken ct) =>
        {
            if (proposed.JobId != Guid.Empty) return Problem(400, "ValidationFailed", "jobId");
            try
            {
                var receipt = await commands.SaveAsync(proposed with { JobId = Guid.NewGuid(), Revision = 0 }, null,
                    SessionEndpoints.Actor(context), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapGet("/api/jobs/{id:guid}/configuration", async (Guid id, IJobCommands commands, HttpResponse response, CancellationToken ct) =>
        {
            try
            {
                var editor = await commands.GetEditableAsync(id, ct);
                response.Headers.ETag = '"' + Convert.ToBase64String(editor.Version) + '"';
                return Results.Ok(editor);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapPut("/api/jobs/{id:guid}", async (Guid id, JobRevision proposed, IJobCommands commands, HttpContext context, CancellationToken ct) =>
        {
            if (proposed.JobId != id) return Problem(400, "ValidationFailed", "jobId");
            var value = context.Request.Headers.IfMatch.ToString();
            if (value.Length < 3 || value[0] != '"' || value[^1] != '"')
                return Problem(412, "InvalidIfMatch", "headers.ifMatch");
            byte[] expected;
            try { expected = Convert.FromBase64String(value[1..^1]); }
            catch (FormatException) { return Problem(412, "InvalidIfMatch", "headers.ifMatch"); }
            try
            {
                var receipt = await commands.SaveAsync(proposed with { Revision = 0 }, expected,
                    SessionEndpoints.Actor(context), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapPost("/api/jobs/{id:guid}/runs", async (Guid id, RunNowRequest input, IJobCommands commands, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                var receipt = await commands.RunNowAsync(id, input.RequestKey, SessionEndpoints.Actor(context), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapGet("/api/commands/{id:guid}", async (Guid id, IJobCommands commands, CancellationToken ct) =>
        {
            try { return Results.Ok(await commands.GetCommandAsync(id, ct)); }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Read);
    }

    private static IResult Error(WorkerDockException error) => error.Code switch
    {
        "ValidationFailed" or "UnknownTemplate" => Problem(400, error.Code, error.Field),
        "JobNotFound" or "CommandNotFound" => Problem(404, error.Code),
        "StaleVersion" => Problem(412, error.Code, "headers.ifMatch"),
        "JobExists" or "JobDisabled" or "JobArchived" or "ProfileUnavailable" or "RequestKeyConflict" => Problem(409, error.Code),
        _ => Problem(503, "PersistenceUnavailable")
    };

    private static IResult Problem(int status, string code, string? field = null) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["field"] = field ?? ""
        });
}
