using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Authentication;
namespace WorkerDock.Web.Endpoints;

public sealed record SetCapacityRequest(int Limit);
public sealed record SetJobControlsRequest(int Limit, bool PauseOnNeedsReview);
public static class CapacityEndpoints
{
    public static void MapCapacity(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/capacity",async (IMonitoringQueries queries,CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.WorkersAsync(ct)); }
            catch(WorkerDockException e) { return Results.Problem(statusCode:503,extensions:new Dictionary<string,object?> { ["code"]=e.Code }); }
        }).RequireAuthorization(AccessPolicies.Read);
        app.MapPut("/api/jobs/{id:guid}/execution-controls", (Guid id, SetJobControlsRequest input, ICapacityCommands commands, HttpContext context, CancellationToken ct) =>
            Apply(() => commands.SetJobControlsAsync(id,input.Limit,input.PauseOnNeedsReview,SessionEndpoints.Actor(context),ct))).RequireAuthorization(AccessPolicies.Manage);
        app.MapPut("/api/jobs/{id:guid}/limit", (Guid id, SetCapacityRequest input, ICapacityCommands commands, HttpContext context, CancellationToken ct) =>
            Apply(() => commands.SetJobLimitAsync(id,input.Limit,SessionEndpoints.Actor(context),ct))).RequireAuthorization(AccessPolicies.Manage);
    }
    private static async Task<IResult> Apply(Func<ValueTask<CommandReceipt>> action)
    {
        try { var receipt = await action(); return Results.Accepted($"/api/commands/{receipt.CommandId}",receipt); }
        catch (WorkerDockException e)
        {
            return Results.Problem(statusCode:e.Code switch { "ValidationFailed" => 400, "UnknownTemplate" or "JobNotFound" or "WorkerNotFound" => 404, _ => 503 },
                extensions:new Dictionary<string,object?> { ["code"]=e.Code, ["field"]=e.Field ?? "" });
        }
    }
}
