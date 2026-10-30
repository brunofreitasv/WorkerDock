using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Authentication;
using WorkerDock.Infrastructure.Scheduling;

namespace WorkerDock.Web.Endpoints;

public sealed record SaveScheduleRequest(ScheduleEditorInput Input, MissedWorkPolicy Policy);
public sealed record SchedulePreviewRequest(ScheduleEditorInput Input, DateTimeOffset AfterUtc, int Count);

public static class ScheduleEndpoints
{
    public static void MapSchedules(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/time-zones", () => Results.Ok(TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .RequireAuthorization(AccessPolicies.Read);

        app.MapGet("/api/jobs/{id:guid}/schedule", async (Guid id, IScheduleCommands commands, HttpResponse response, CancellationToken ct) =>
        {
            try { var schedule = await commands.GetAsync(id, ct); response.Headers.ETag = ETag(schedule.Version); return Results.Ok(new { input = ScheduleInputConverter.ToInput(schedule.Definition), resolvedUtc = schedule.Definition.OneTimeUtc, schedule.Policy, schedule.Paused }); }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapPut("/api/jobs/{id:guid}/schedule", async (Guid id, SaveScheduleRequest request, IScheduleCommands commands, HttpRequest http, CancellationToken ct) =>
        {
            if (!TryExpectedVersion(http, out var version)) return Problem(412, "InvalidIfMatch", "headers.ifMatch");
            try
            {
                var definition = ScheduleInputConverter.ConvertInput(request.Input);
                var receipt = await commands.SaveAsync(id, definition, request.Policy, version, SessionEndpoints.Actor(http.HttpContext), ct);
                return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt);
            }
            catch (WorkerDockException error) { return Error(error); }
            catch (TimeZoneNotFoundException) { return Problem(400, "ValidationFailed", "zoneId"); }
            catch (InvalidTimeZoneException) { return Problem(400, "ValidationFailed", "zoneId"); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapDelete("/api/jobs/{id:guid}/schedule", async (Guid id, IScheduleCommands commands, HttpRequest http, CancellationToken ct) =>
        {
            if (!TryExpectedVersion(http, out var version)) return Problem(412, "InvalidIfMatch", "headers.ifMatch");
            try { var receipt = await commands.DeleteAsync(id, version, SessionEndpoints.Actor(http.HttpContext), ct); return Results.Accepted($"/api/commands/{receipt.CommandId}", receipt); }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);

        app.MapPost("/api/schedules/preview", async (SchedulePreviewRequest request, IScheduleCommands commands, CancellationToken ct) =>
        {
            try
            {
                var definition = ScheduleInputConverter.ConvertInput(request.Input);
                var items = await commands.PreviewAsync(definition, request.AfterUtc, request.Count, ct);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(definition.ZoneId);
                return Results.Ok(items.Select(utc => new SchedulePreviewItem(utc, zone.GetUtcOffset(utc.UtcDateTime))));
            }
            catch (WorkerDockException error) { return Error(error); }
            catch (TimeZoneNotFoundException) { return Problem(400, "ValidationFailed", "zoneId"); }
            catch (InvalidTimeZoneException) { return Problem(400, "ValidationFailed", "zoneId"); }
        }).RequireAuthorization(AccessPolicies.Manage);
    }

    private static bool TryExpectedVersion(HttpRequest request, out byte[] version)
    {
        version = [];
        var tag = request.Headers.IfMatch.ToString();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"') return false;
        try { version = Convert.FromBase64String(tag[1..^1]); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
    private static IResult Error(WorkerDockException error) => error.Code switch
    {
        "ValidationFailed" => Problem(400, error.Code, error.Field),
        "JobNotFound" or "ScheduleNotFound" => Problem(404, error.Code),
        "StaleVersion" => Problem(412, error.Code, "headers.ifMatch"),
        "JobArchived" => Problem(409, error.Code),
        _ => Problem(503, "PersistenceUnavailable")
    };
    private static IResult Problem(int status, string code, string? field = null) => Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code, ["field"] = field ?? "" });
    private static string ETag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';
}
