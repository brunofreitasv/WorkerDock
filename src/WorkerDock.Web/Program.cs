using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Workers;
using WorkerDock.Application.Tenants;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Domain.Tenants;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Authentication;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkerDockAccess();
builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.TryAddSingleton<IJobCommands>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (!Guid.TryParse(config["Tenant:InstanceId"], out var instanceId)) return new UnavailableJobCommands();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryJobCommands(provider.GetRequiredService<TimeProvider>()) : new UnavailableJobCommands();
    var connection = config["Tenant:ConnectionString"];
    return !string.IsNullOrWhiteSpace(connection)
        ? new SqlJobCommands(new(connection, instanceId))
        : new UnavailableJobCommands();
});
builder.Services.TryAddSingleton<ITenantSettingsService>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (!Guid.TryParse(config["Tenant:InstanceId"], out var instanceId)) return new UnavailableTenantSettingsService();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryTenantSettingsService(instanceId) : new UnavailableTenantSettingsService();
    var connection = config["Tenant:ConnectionString"];
    return !string.IsNullOrWhiteSpace(connection) ? new TenantSettingsService(new(connection, instanceId)) : new UnavailableTenantSettingsService();
});
builder.Services.TryAddSingleton<IExecutionControls>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    if (config["Tenant:Adapter"] == "Memory")
        return provider.GetRequiredService<IHostEnvironment>().IsDevelopment() && provider.GetRequiredService<IJobCommands>() is IExecutionControls controls
            ? controls : new UnavailableExecutionControls();
    return Guid.TryParse(config["Tenant:InstanceId"], out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlExecutionControls(new(config["Tenant:ConnectionString"]!, id)) : new UnavailableExecutionControls();
});
builder.Services.TryAddSingleton<ICapacityStore>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory" && Guid.TryParse(config["Tenant:InstanceId"],out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlCapacityStore(new(config["Tenant:ConnectionString"]!,id)) : new UnavailableCapacityStore();
});
builder.Services.TryAddSingleton<ICapacityCommands>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory" && Guid.TryParse(config["Tenant:InstanceId"], out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlCapacityCommands(new(config["Tenant:ConnectionString"]!, id)) : new UnavailableCapacityCommands();
});
var app = builder.Build();
var access = app.Services.GetRequiredService<AccessOptions>();
if (!access.AuthenticationConfigured)
    app.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().RemoveScheme("oidc");
app.UseExceptionHandler(handler => handler.Run(context => SafeProblem(500, "RequestFailed").ExecuteAsync(context)));
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseAuthentication();
app.UseWorkerDockSessions();
app.UseAuthorization();
app.MapWorkerDockSessions();
app.MapJobCommands();
app.MapExecutionControls();
app.MapCapacity();
app.UseDefaultFiles();
app.UseStaticFiles();

static IResult SafeProblem(int status, string code, string field = "") =>
    Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code, ["field"] = field });
static string ETag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';

app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
app.MapGet("/health/ready", async (ITenantSettingsService service, CancellationToken ct) =>
{
    try { await service.GetAsync(ct); return Results.Json(new { status = "ready" }); }
    catch { return Results.Json(new { status = "unavailable" }, statusCode: 503); }
});

app.MapGet("/api/settings", async (HttpResponse response, ITenantSettingsService service, CancellationToken ct) =>
{
    try
    {
        var settings = await service.GetAsync(ct);
        response.Headers.ETag = ETag(settings.Version);
        return Results.Ok(new { settings.DisplayName, settings.DisplayTimeZone, Version = Convert.ToBase64String(settings.Version) });
    }
    catch { return SafeProblem(503, "PersistenceUnavailable"); }
}).RequireAuthorization(AccessPolicies.Manage);

app.MapPut("/api/settings", async (HttpRequest request, SettingsUpdate input, ITenantSettingsService service, CancellationToken ct) =>
{
    if (!request.Headers.TryGetValue("If-Match", out var etag)) return SafeProblem(412, "InvalidIfMatch", "headers.ifMatch");
    byte[] expected;
    var tag = etag.ToString();
    if (tag.Length < 3 || tag[0] != '"' || tag[^1] != '"') return SafeProblem(412, "InvalidIfMatch", "headers.ifMatch");
    try { expected = Convert.FromBase64String(tag[1..^1]); }
    catch (FormatException) { return SafeProblem(412, "InvalidIfMatch", "headers.ifMatch"); }
    try
    {
        var current = await service.GetAsync(ct);
        var updated = await service.UpdateAsync(current with { DisplayName = input.DisplayName, DisplayTimeZone = input.DisplayTimeZone }, expected, SessionEndpoints.Actor(request.HttpContext), ct);
        request.HttpContext.Response.Headers.ETag = ETag(updated.Version);
        return Results.Ok(new { updated.DisplayName, updated.DisplayTimeZone, Version = Convert.ToBase64String(updated.Version) });
    }
    catch (WorkerDockException error) when (error.Code == "StaleVersion") { return SafeProblem(412, error.Code, "headers.ifMatch"); }
    catch (WorkerDockException error) when (error.Code == "ValidationFailed") { return SafeProblem(400, error.Code, error.Field ?? ""); }
    catch { return SafeProblem(503, "PersistenceUnavailable"); }
}).RequireAuthorization(AccessPolicies.Manage);

// Task02 provides policy-protected route contracts. Operational projections arrive in later stages.
foreach (var route in new[] { "/api/dashboard", "/api/monitoring", "/api/audit" })
    app.MapGet(route, () => Results.Ok(new { available = false, items = Array.Empty<object>() })).RequireAuthorization(AccessPolicies.Read);
app.Map("/api/{**path}", () => Results.NotFound());
app.Map("/auth/{**path}", () => Results.NotFound());
app.Map("/_test/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program;
public sealed record SettingsUpdate(string DisplayName, string DisplayTimeZone);
internal sealed class UnavailableTenantSettingsService : ITenantSettingsService
{
    public ValueTask<TenantSettings> GetAsync(CancellationToken ct) => ValueTask.FromException<TenantSettings>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<TenantSettings> UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct) => ValueTask.FromException<TenantSettings>(new WorkerDockException("PersistenceUnavailable"));
}
internal sealed class UnavailableJobCommands : IJobCommands
{
    public ValueTask<CommandReceipt> SaveAsync(WorkerDock.Domain.Jobs.JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
        => ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ActorContext actor, CancellationToken ct)
        => ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> GetCommandAsync(Guid commandId, CancellationToken ct)
        => ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<JobEditorDto> GetEditableAsync(Guid jobId, CancellationToken ct)
        => ValueTask.FromException<JobEditorDto>(new WorkerDockException("PersistenceUnavailable"));
}

internal sealed class UnavailableCapacityCommands : ICapacityCommands
{
    private static ValueTask<CommandReceipt> Unavailable() => ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> SetTemplateLimitAsync(string id,int limit,ActorContext actor,CancellationToken ct) => Unavailable();
    public ValueTask<CommandReceipt> SetJobLimitAsync(Guid id,int limit,ActorContext actor,CancellationToken ct) => Unavailable();
    public ValueTask<CommandReceipt> SetWorkerLimitAsync(Guid id,int limit,ActorContext actor,CancellationToken ct) => Unavailable();
}

internal sealed class UnavailableCapacityStore : ICapacityStore
{
    public ValueTask<T> TransactAsync<T>(Func<CapacityState,DateTimeOffset,T> change,CancellationToken ct) => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
}
