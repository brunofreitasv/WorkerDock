using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Authentication;
namespace WorkerDock.Web.Endpoints;

public sealed record SetCapacityRequest(int Limit);
public static class CapacityEndpoints
{
    public static void MapCapacity(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/capacity",async (ICapacityStore store,CancellationToken ct) =>
        {
            try
            {
                var view=await store.TransactAsync((state,_) => new { Policy=state.Snapshot,Workers=state.Workers.Values.ToArray() },ct);
                return Results.Ok(view);
            }
            catch(WorkerDockException e) { return Results.Problem(statusCode:503,extensions:new Dictionary<string,object?> { ["code"]=e.Code }); }
        }).RequireAuthorization(AccessPolicies.Read);
        app.MapPut("/api/templates/{id}/limit", (string id, SetCapacityRequest input, ICapacityCommands commands, HttpContext context, CancellationToken ct) =>
            Apply(() => commands.SetTemplateLimitAsync(id,input.Limit,SessionEndpoints.Actor(context),ct))).RequireAuthorization(AccessPolicies.Manage);
        app.MapPut("/api/jobs/{id:guid}/limit", (Guid id, SetCapacityRequest input, ICapacityCommands commands, HttpContext context, CancellationToken ct) =>
            Apply(() => commands.SetJobLimitAsync(id,input.Limit,SessionEndpoints.Actor(context),ct))).RequireAuthorization(AccessPolicies.Manage);
        app.MapPut("/api/workers/{id:guid}/limit", (Guid id, SetCapacityRequest input, ICapacityCommands commands, HttpContext context, CancellationToken ct) =>
            Apply(() => commands.SetWorkerLimitAsync(id,input.Limit,SessionEndpoints.Actor(context),ct))).RequireAuthorization(AccessPolicies.Manage);
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
