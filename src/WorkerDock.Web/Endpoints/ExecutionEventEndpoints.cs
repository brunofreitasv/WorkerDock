using System.Globalization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Observability;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

public static class ExecutionEventEndpoints
{
    public static void MapExecutionEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/monitoring/executions/{executionId:guid}/events", async (Guid executionId, HttpRequest request, IExecutionEventQueries queries, CancellationToken ct) =>
        {
            var rawLimit = request.Query["limit"].FirstOrDefault();
            var limit = rawLimit is null ? 50 : int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
            var cursor = request.Query["cursor"].FirstOrDefault();
            if (executionId == Guid.Empty || limit is < 1 or > 100 || cursor is { Length: > 128 }) return Invalid();
            try { return Results.Ok(await queries.ReadAsync(executionId, cursor, limit, ct)); }
            catch (WorkerDockException error) when (error.Code == "ValidationFailed") { return Invalid(); }
            catch (WorkerDockException error) when (error.Code == "ExecutionNotFound") { return Results.Problem(statusCode: 404, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);
    }

    private static IResult Invalid() => Results.Problem(statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "ValidationFailed" });
}
