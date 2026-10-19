using System.Globalization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

public static class MonitoringEndpoints
{
    public static void MapMonitoring(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/monitoring", async (IMonitoringQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.DashboardAsync(ct)); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);

        endpoints.MapGet("/api/dashboard", async (IMonitoringQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.DashboardAsync(ct)); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);

        endpoints.MapGet("/api/monitoring/jobs", async (HttpRequest request, IMonitoringQueries queries, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryPage(request, out var page) || !TryFilter(request, clock, out var filter)) return ValidationProblem();
            try { return Results.Ok(await queries.JobsAsync(filter!, page!, ct)); }
            catch (WorkerDockException error) when (error.Code == "ValidationFailed") { return ValidationProblem(); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);

        endpoints.MapGet("/api/monitoring/executions/{executionId:guid}", async (Guid executionId, IMonitoringQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.ExecutionAsync(executionId, ct)); }
            catch (WorkerDockException error) when (error.Code == "ExecutionNotFound") { return Results.Problem(statusCode: 404, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);

        endpoints.MapGet("/api/monitoring/workers", async (IMonitoringQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.WorkersAsync(ct)); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);

        endpoints.MapGet("/api/audit", async (HttpRequest request, IMonitoringQueries queries, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryPage(request, out var page) || !TryFilter(request, clock, out var filter)) return ValidationProblem();
            try { return Results.Ok(await queries.AuditAsync(filter!, page!, ct)); }
            catch (WorkerDockException error) when (error.Code == "ValidationFailed") { return ValidationProblem(); }
            catch { return Results.Problem(statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "MonitoringUnavailable" }); }
        }).RequireAuthorization(AccessPolicies.Read);
    }

    private static bool TryPage(HttpRequest request, out PageRequest? page)
    {
        page = null;
        var cursor = request.Query["cursor"].FirstOrDefault();
        var rawLimit = request.Query["limit"].FirstOrDefault();
        var limit = rawLimit is null ? 50 : int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        if (limit is < 1 or > 100 || cursor is { Length: > 128 }) return false;
        page = new(cursor, limit); return true;
    }

    private static bool TryFilter(HttpRequest request, TimeProvider clock, out MonitoringFilter? filter)
    {
        filter = null;
        var now = clock.GetUtcNow();
        var rawFrom = request.Query["fromUtc"].FirstOrDefault();
        var rawTo = request.Query["toUtc"].FirstOrDefault();
        var parsedFrom = ParseDate(rawFrom);
        var parsedTo = ParseDate(rawTo);
        if ((rawFrom is not null && parsedFrom is null) || (rawTo is not null && parsedTo is null)) return false;
        var from = parsedFrom ?? now.AddDays(-7);
        var to = parsedTo ?? now;
        var search = request.Query["search"].FirstOrDefault();
        var state = request.Query["state"].FirstOrDefault();
        var template = request.Query["templateId"].FirstOrDefault();
        if (from > to || to - from > TimeSpan.FromDays(90) || search is { Length: > 100 } || template is { Length: > 100 } || state is not (null or "Enabled" or "Disabled" or "Archived")) return false;
        filter = new(from, to, string.IsNullOrWhiteSpace(search) ? null : search, state, string.IsNullOrWhiteSpace(template) ? null : template); return true;
    }

    private static DateTimeOffset? ParseDate(string? value) => value is null ? null : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;
    private static IResult ValidationProblem() => Results.Problem(statusCode: 400, extensions: new Dictionary<string, object?> { ["code"] = "ValidationFailed" });
}
