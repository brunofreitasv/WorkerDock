using System.Text.Json.Serialization;
using System.Globalization;
using WorkerDock.Application.Monitoring;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Domain.Alerts;
using WorkerDock.Infrastructure.Authentication;

namespace WorkerDock.Web.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AlertRuleUpdate(Guid Id, string Name, string Condition, int Threshold, int WindowSeconds,
    int GraceSeconds, int ReminderSeconds, Guid SinkId, bool Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AlertSinkUpdate(Guid Id, string Name, string Type, System.Text.Json.JsonElement Configuration,
    int MaxAttempts, int RetryBaseDelaySeconds, int RetryMaxDelaySeconds, int TimeoutSeconds, bool Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AlertRetryRequest(int AdditionalAttempts);

public static class AlertEndpoints
{
    public static void MapAlerts(this IEndpointRouteBuilder routes)
    {
        var ruleRoutes = routes.MapGroup("/api/alert-rules").RequireAuthorization(AccessPolicies.Manage);
        ruleRoutes.MapGet("", async (IAlertRules rules, CancellationToken ct) =>
        {
            try { return Results.Ok((await rules.ListAsync(ct)).Select(Editor)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        ruleRoutes.MapGet("/{id:guid}", async (Guid id, HttpResponse response, IAlertRules rules, CancellationToken ct) =>
        {
            try { var value = await rules.GetAsync(id, ct); response.Headers.ETag = Tag(value.Version); return Results.Ok(Editor(value)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        ruleRoutes.MapPut("/{id:guid}", async (Guid id, HttpContext context, AlertRuleUpdate input, IAlertRules rules, CancellationToken ct) =>
        {
            if (id != input.Id) return Problem(400, "ValidationFailed", "id");
            byte[]? expected;
            try { expected = Version(context, required: false); }
            catch (WorkerDockException error) { return Error(error); }
            try
            {
                var command = await rules.SaveAsync(new(input.Id, input.Name, input.Condition, input.Threshold,
                    TimeSpan.FromSeconds(input.WindowSeconds), TimeSpan.FromSeconds(input.GraceSeconds),
                    TimeSpan.FromSeconds(input.ReminderSeconds), input.SinkId, input.Enabled), expected,
                    SessionEndpoints.Actor(context), ct);
                var saved = await rules.GetAsync(id, ct); context.Response.Headers.ETag = Tag(saved.Version);
                return Results.Ok(new { saved.Rule, Version = Convert.ToBase64String(saved.Version), commandId = command.CommandId });
            }
            catch (WorkerDockException error) { return Error(error); }
        });

        var sinkRoutes = routes.MapGroup("/api/alert-sinks").RequireAuthorization(AccessPolicies.Manage);
        sinkRoutes.MapGet("", async (IAlertSinks sinks, CancellationToken ct) =>
        {
            try { return Results.Ok((await sinks.ListAsync(ct)).Select(Editor)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        sinkRoutes.MapGet("/{id:guid}", async (Guid id, HttpResponse response, IAlertSinks sinks, CancellationToken ct) =>
        {
            try { var value = await sinks.GetAsync(id, ct); response.Headers.ETag = Tag(value.Version); return Results.Ok(Editor(value)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        sinkRoutes.MapPut("/{id:guid}", async (Guid id, HttpContext context, AlertSinkUpdate input, IAlertSinks sinks, CancellationToken ct) =>
        {
            if (id != input.Id) return Problem(400, "ValidationFailed", "id");
            byte[]? expected;
            try { expected = Version(context, required: false); }
            catch (WorkerDockException error) { return Error(error); }
            try
            {
                var definition = new AlertSinkDefinition(input.Id, input.Name, input.Type, input.Configuration,
                    input.MaxAttempts, TimeSpan.FromSeconds(input.RetryBaseDelaySeconds), TimeSpan.FromSeconds(input.RetryMaxDelaySeconds),
                    TimeSpan.FromSeconds(input.TimeoutSeconds), input.Enabled);
                var command = await sinks.SaveAsync(definition, expected, SessionEndpoints.Actor(context), ct);
                var saved = await sinks.GetAsync(id, ct); context.Response.Headers.ETag = Tag(saved.Version);
                return Results.Ok(new { saved.Definition, Version = Convert.ToBase64String(saved.Version), commandId = command.CommandId });
            }
            catch (WorkerDockException error) { return Error(error); }
        });

        var views = routes.MapGroup("/api/alerts").RequireAuthorization(AccessPolicies.Read);
        views.MapGet("/rule-options", async (IAlertQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.RuleOptionsAsync(ct)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        views.MapGet("/incidents", async (HttpRequest request, TimeProvider clock, IAlertQueries queries, CancellationToken ct) =>
        {
            if (!TryPage(request, out var page) || !TryIncidentFilter(request, clock, out var filter)) return ValidationProblem();
            try { return Results.Ok(await queries.IncidentsAsync(filter!, page!, ct)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        views.MapGet("/deliveries", async (HttpRequest request, TimeProvider clock, IAlertQueries queries, CancellationToken ct) =>
        {
            if (!TryPage(request, out var page) || !TryDeliveryFilter(request, clock, out var filter)) return ValidationProblem();
            try { return Results.Ok(await queries.DeliveriesAsync(filter!, page!, ct)); }
            catch (WorkerDockException error) { return Error(error); }
        });
        routes.MapPost("/api/alerts/deliveries/{id:guid}/retry", async (Guid id, AlertRetryRequest input, HttpContext context,
            IAlertDeliveryStore deliveries, CancellationToken ct) =>
        {
            try
            {
                var receipt = await deliveries.RetryFailedAsync(id, input.AdditionalAttempts, SessionEndpoints.Actor(context), ct);
                return Results.Accepted(value: new { deliveryId = id, commandId = receipt.CommandId });
            }
            catch (WorkerDockException error) { return Error(error); }
        }).RequireAuthorization(AccessPolicies.Manage);
    }

    private static object Editor(AlertRuleEditorDto value) => new
    {
        value.Rule.Id, value.Rule.Name, value.Rule.Condition, value.Rule.Threshold,
        WindowSeconds = checked((int)value.Rule.Window.TotalSeconds), GraceSeconds = checked((int)value.Rule.Grace.TotalSeconds),
        ReminderSeconds = checked((int)value.Rule.ReminderInterval.TotalSeconds), value.Rule.SinkId, value.Rule.Enabled,
        Version = Convert.ToBase64String(value.Version)
    };

    private static object Editor(AlertSinkEditorDto value) => new
    {
        value.Definition.Id, value.Definition.Name, value.Definition.Type, value.Definition.Configuration,
        value.Definition.MaxAttempts, RetryBaseDelaySeconds = checked((int)value.Definition.RetryBaseDelay.TotalSeconds),
        RetryMaxDelaySeconds = checked((int)value.Definition.RetryMaxDelay.TotalSeconds),
        TimeoutSeconds = checked((int)value.Definition.Timeout.TotalSeconds), value.Definition.Enabled,
        Version = Convert.ToBase64String(value.Version)
    };

    private static bool TryPage(HttpRequest request, out PageRequest? page)
    {
        page = null;
        var rawLimit = request.Query["limit"].FirstOrDefault();
        var limit = rawLimit is null ? 50 : int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var cursor = request.Query["cursor"].FirstOrDefault();
        if (request.Query["limit"].Count > 1 || request.Query["cursor"].Count > 1 || limit is < 1 or > 100 || cursor is { Length: > 2048 }) return false;
        page = new(cursor, limit); return true;
    }

    private static bool TryIncidentFilter(HttpRequest request, TimeProvider clock, out IncidentFilter? filter)
    {
        filter = null;
        var state = request.Query["state"].FirstOrDefault() ?? "Active";
        if (state is not ("Active" or "All" or "Resolved")) return false;
        var rawFrom = request.Query["fromUtc"].FirstOrDefault(); var rawTo = request.Query["toUtc"].FirstOrDefault();
        var from = ParseDate(rawFrom); var to = ParseDate(rawTo);
        if ((rawFrom is not null && from is null) || (rawTo is not null && to is null) || (rawFrom is null) != (rawTo is null)) return false;
        var now = clock.GetUtcNow().ToUniversalTime();
        if (rawFrom is null && state != "Active") { from = now.AddDays(-30); to = now; }
        if (from is { } start && to is { } end && (end <= start || end - start > TimeSpan.FromDays(90))) return false;
        var rawRule = request.Query["ruleId"].FirstOrDefault(); Guid? ruleId = null;
        if (rawRule is not null) { if (!Guid.TryParse(rawRule, out var parsed) || parsed == Guid.Empty) return false; ruleId = parsed; }
        if (request.Query["state"].Count > 1 || request.Query["fromUtc"].Count > 1 || request.Query["toUtc"].Count > 1 || request.Query["ruleId"].Count > 1) return false;
        filter = new(from, to, state, ruleId); return true;
    }

    private static bool TryDeliveryFilter(HttpRequest request, TimeProvider clock, out DeliveryFilter? filter)
    {
        filter = null; var now = clock.GetUtcNow().ToUniversalTime();
        var rawFrom = request.Query["fromUtc"].FirstOrDefault(); var rawTo = request.Query["toUtc"].FirstOrDefault();
        var from = ParseDate(rawFrom); var to = ParseDate(rawTo);
        if ((rawFrom is not null && from is null) || (rawTo is not null && to is null) || (rawFrom is null) != (rawTo is null)) return false;
        from ??= now.AddDays(-30); to ??= now;
        if (to <= from || to - from > TimeSpan.FromDays(90)) return false;
        var status = request.Query["status"].FirstOrDefault(); var kind = request.Query["kind"].FirstOrDefault();
        if (status is not (null or "Pending" or "Leased" or "Delivered" or "Failed" or "Cancelled") ||
            kind is not (null or "Open" or "Reminder" or "Resolve") || request.Query["status"].Count > 1 || request.Query["kind"].Count > 1 ||
            request.Query["fromUtc"].Count > 1 || request.Query["toUtc"].Count > 1) return false;
        filter = new(from.Value, to.Value, status, kind); return true;
    }

    private static DateTimeOffset? ParseDate(string? value) => value is null ? null : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private static IResult ValidationProblem() => Results.Problem(statusCode: 400,
        extensions: new Dictionary<string, object?> { ["code"] = "ValidationFailed" });
    private static byte[]? Version(HttpContext context, bool required)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (header.Length == 0 && !required) return null;
        if (header.Length < 3 || header[0] != '"' || header[^1] != '"') throw new WorkerDockException("StaleVersion");
        try { var value = Convert.FromBase64String(header[1..^1]); return value.Length == 8 ? value : throw new WorkerDockException("StaleVersion"); }
        catch (FormatException) { throw new WorkerDockException("StaleVersion"); }
    }

    private static string Tag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';
    private static IResult Error(WorkerDockException error) => Problem(error.Code switch
    {
        "StaleVersion" => 412,
        "ValidationFailed" => 400,
        "AlertRuleNotFound" or "AlertSinkNotFound" => 404,
        "AlertSinkUnavailable" => 409,
        "AlertDeliveryNotFound" => 404,
        "AlertDeliveryNotRetryable" or "AlertRetryBudgetExceeded" => 409,
        "PersistenceUnavailable" or "TenantInstanceMismatch" => 503,
        _ => 400
    }, error.Code, error.Field ?? "");
    private static IResult Problem(int status, string code, string field) => Results.Problem(statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["field"] = field });
}
