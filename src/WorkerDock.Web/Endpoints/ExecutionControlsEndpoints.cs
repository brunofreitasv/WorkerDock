using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

public sealed record StopExecutionRequest(bool Force, bool ConfirmForce, string Reason);
public sealed record ResolveExecutionRequest(string Decision, string Reason);

public static class ExecutionControlsEndpoints
{
    public static void MapExecutionControls(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/executions/{id:guid}/stop", async (Guid id, StopExecutionRequest input, IExecutionControls controls, HttpContext context, CancellationToken ct) =>
        {
            if (input.Force && !input.ConfirmForce) return Error(new("ValidationFailed", "confirmForce"));
            try
            {
                var receipt = await controls.StopAsync(id, input.Force, input.Reason, SessionEndpoints.Actor(context), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException e) { return Error(e); }
        }).RequireAuthorization(AccessPolicies.Manage);
        app.MapPost("/api/executions/{id:guid}/resolve", async (Guid id, ResolveExecutionRequest input, IExecutionControls controls, HttpContext context, CancellationToken ct) =>
        {
            if (input.Decision is not ("ReconcileSucceeded" or "Cancel" or "AuthorizeRetry")) return Error(new("ValidationFailed", "decision"));
            try
            {
                var receipt = await controls.ResolveAsync(id, input.Decision, input.Reason, SessionEndpoints.Actor(context), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException e) { return Error(e); }
        }).RequireAuthorization(AccessPolicies.Manage);
    }
    private static IResult Error(WorkerDockException e) => Results.Problem(statusCode: e.Code switch
    {
        "ValidationFailed" => 400, "ExecutionNotFound" => 404,
        "ExecutionNotReviewable" or "AttemptBudgetExhausted" => 409, _ => 503
    }, extensions: new Dictionary<string, object?> { ["code"] = e.Code, ["field"] = e.Field ?? "" });
}
