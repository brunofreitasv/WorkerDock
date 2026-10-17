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
    app.MapGet("/_test/settings", async (CancellationToken ct) =>
    {
        var service = ResolveSettings();
        if (service is null)
            return Results.StatusCode(503);
        try
        {
            var settings = await service.GetAsync(ct);
            return Results.Ok(new { settings.DisplayName, settings.DisplayTimeZone, Version = Convert.ToBase64String(settings.Version) });
        }
        catch { return Results.StatusCode(503); }
    });

    app.MapPut("/_test/settings", async (HttpRequest request, TestSettingsUpdate input, CancellationToken ct) =>
    {
        if (request.Headers["X-WorkerDock-CSRF"] != "development") return Results.StatusCode(403);
        var service = ResolveSettings();
        if (service is null) return Results.StatusCode(503);
        if (!request.Headers.TryGetValue("If-Match", out var etag)) return Results.StatusCode(412);
        byte[] expected;
        try { expected = Convert.FromBase64String(etag.ToString().Trim('"')); }
        catch (FormatException) { return Results.StatusCode(412); }
        try
        {
            var current = await service.GetAsync(ct);
            var updated = await service.UpdateAsync(current with { DisplayName = input.DisplayName, DisplayTimeZone = input.DisplayTimeZone },
                expected, testActor with { CorrelationId = Guid.NewGuid() }, ct);
            return Results.Ok(new { updated.DisplayName, updated.DisplayTimeZone, Version = Convert.ToBase64String(updated.Version) });
        }
        catch (WorkerDockException error) when (error.Code == "StaleVersion") { return Results.StatusCode(412); }
        catch (WorkerDockException error) when (error.Code == "ValidationFailed") { return Results.BadRequest(new { code = error.Code, field = error.Field }); }
        catch { return Results.StatusCode(503); }
    });
}

app.Run();
public partial class Program;
public sealed record TestSettingsUpdate(string DisplayName, string DisplayTimeZone);
