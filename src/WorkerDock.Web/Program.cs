using WorkerDock.Application.Common;
using WorkerDock.Application.Tenants;
using WorkerDock.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
Guid memoryId = Guid.Empty;
var memoryEnabled = app.Environment.IsDevelopment() && app.Configuration["Tenant:Adapter"] == "Memory" &&
    Guid.TryParse(app.Configuration["Tenant:InstanceId"], out memoryId);
ITenantSettingsService? memoryService = memoryEnabled ? new InMemoryTenantSettingsService(memoryId) : null;
var testActor = new ActorContext("session:" + Guid.NewGuid().ToString("N"), "Anonymous", Guid.NewGuid());

ITenantSettingsService? ResolveSettings()
{
    if (memoryService is not null) return memoryService;
    var connection = app.Configuration["Tenant:ConnectionString"];
    return !string.IsNullOrWhiteSpace(connection) && Guid.TryParse(app.Configuration["Tenant:InstanceId"], out var id)
        ? new TenantSettingsService(new(connection, id)) : null;
}

static IResult SafeProblem(int status, string code, string field = "") =>
    Results.Problem(statusCode: status, extensions: new Dictionary<string, object?>
    {
        ["code"] = code,
        ["field"] = field
    });

static string ETag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';

app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
app.MapGet("/health/ready", async (CancellationToken ct) =>
{
    var service = ResolveSettings();
    if (service is null)
        return Results.Json(new { status = "unavailable" }, statusCode: 503);
    try
    {
        await service.GetAsync(ct);
        return Results.Json(new { status = "ready" });
    }
    catch { return Results.Json(new { status = "unavailable" }, statusCode: 503); }
});

if (app.Environment.IsDevelopment())
{
    app.MapGet("/_test/settings", async (HttpResponse response, CancellationToken ct) =>
    {
        var service = ResolveSettings();
        if (service is null)
            return SafeProblem(503, "PersistenceUnavailable");
        try
        {
            var settings = await service.GetAsync(ct);
            response.Headers.ETag = ETag(settings.Version);
            return Results.Ok(new { settings.DisplayName, settings.DisplayTimeZone, Version = Convert.ToBase64String(settings.Version) });
        }
        catch { return SafeProblem(503, "PersistenceUnavailable"); }
    });

    app.MapPut("/_test/settings", async (HttpRequest request, TestSettingsUpdate input, CancellationToken ct) =>
    {
        if (request.Headers["X-WorkerDock-CSRF"] != "development")
            return SafeProblem(403, "Forbidden", "headers.xWorkerDockCsrf");
        var service = ResolveSettings();
        if (service is null) return SafeProblem(503, "PersistenceUnavailable");
        if (!request.Headers.TryGetValue("If-Match", out var etag))
            return SafeProblem(412, "InvalidIfMatch", "headers.ifMatch");
        byte[] expected;
        var tag = etag.ToString();
        if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"')
            return SafeProblem(412, "InvalidIfMatch", "headers.ifMatch");
        try { expected = Convert.FromBase64String(tag[1..^1]); }
        catch (FormatException) { return SafeProblem(412, "InvalidIfMatch", "headers.ifMatch"); }
        try
        {
            var current = await service.GetAsync(ct);
            var updated = await service.UpdateAsync(current with { DisplayName = input.DisplayName, DisplayTimeZone = input.DisplayTimeZone },
                expected, testActor with { CorrelationId = Guid.NewGuid() }, ct);
            request.HttpContext.Response.Headers.ETag = ETag(updated.Version);
            return Results.Ok(new { updated.DisplayName, updated.DisplayTimeZone, Version = Convert.ToBase64String(updated.Version) });
        }
        catch (WorkerDockException error) when (error.Code == "StaleVersion")
        { return SafeProblem(412, error.Code, "headers.ifMatch"); }
        catch (WorkerDockException error) when (error.Code == "ValidationFailed")
        { return SafeProblem(400, error.Code, error.Field ?? ""); }
        catch { return SafeProblem(503, "PersistenceUnavailable"); }
    });
}

app.Run();
public partial class Program;
public sealed record TestSettingsUpdate(string DisplayName, string DisplayTimeZone);
