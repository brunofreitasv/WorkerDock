using WorkerDock.Application.Connections;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Templates;
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
using WorkerDock.Application.Settings;
using WorkerDock.Infrastructure.Settings;
using WorkerDock.Application.Scheduling;
using WorkerDock.Infrastructure.Scheduling;
using WorkerDock.Quartz.Scheduling;
using WorkerDock.Web.Endpoints;
using WorkerDock.Templates.SqlServer;
using WorkerDock.Templates.Http;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Monitoring;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Secrets;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkerDockAccess();
builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.TryAddSingleton<ISecretVault>(_ => SecretVaultRegistration.CreateDevelopment());
builder.Services.TryAddSingleton<IScheduleEvaluator, QuartzScheduleEvaluator>();
var trustedTemplates = new WorkerDock.Contracts.Templates.TemplateDescriptor[]
{
    new SqlTemplate().Descriptor,
    new HttpTemplate(new HttpClient()).Descriptor
};
builder.Services.TryAddSingleton<IJobCommands>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (!Guid.TryParse(config["Tenant:InstanceId"], out var instanceId)) return new UnavailableJobCommands();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryJobCommands(provider.GetRequiredService<TimeProvider>(),profiles:provider.GetRequiredService<IConnectionProfiles>(),integrationSettings:provider.GetRequiredService<IIntegrationSettings>(),templateCatalog:provider.GetRequiredService<ITemplateCatalog>()) : new UnavailableJobCommands();
    var connection = config["Tenant:ConnectionString"];
    return !string.IsNullOrWhiteSpace(connection)
        ? new SqlJobCommands(new(connection, instanceId),provider.GetRequiredService<ITemplateCatalog>())
        : new UnavailableJobCommands();
});
builder.Services.TryAddSingleton<IIntegrationSettings>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryIntegrationSettings() : new UnavailableIntegrationSettings();
    return Guid.TryParse(config["Tenant:InstanceId"], out var instanceId) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlIntegrationSettings(new(config["Tenant:ConnectionString"]!, instanceId)) : new UnavailableIntegrationSettings();
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
builder.Services.TryAddSingleton<ITemplateCatalog>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    if(config["Tenant:Adapter"]=="Memory" && provider.GetRequiredService<IHostEnvironment>().IsDevelopment())
    {
        var catalog=new TemplateCatalog(trustedTemplates);
        catalog.RegisterAsync(trustedTemplates,default).GetAwaiter().GetResult();
        return catalog;
    }
    return config["Tenant:Adapter"]!="Memory" && Guid.TryParse(config["Tenant:InstanceId"],out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlTemplateCatalog(new(config["Tenant:ConnectionString"]!,id),trustedTemplates) : new UnavailableTemplateCatalog();
});
builder.Services.TryAddSingleton<IConnectionProfiles>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    if(config["Tenant:Adapter"]=="Memory" && provider.GetRequiredService<IHostEnvironment>().IsDevelopment())return new InMemoryConnectionProfiles();
    return config["Tenant:Adapter"]!="Memory" && Guid.TryParse(config["Tenant:InstanceId"],out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlConnectionProfiles(new(config["Tenant:ConnectionString"]!,id)) : new UnavailableConnectionProfiles();
});
builder.Services.TryAddSingleton<IProfileDiagnostics>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"]!="Memory" && Guid.TryParse(config["Tenant:InstanceId"],out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlProfileDiagnostics(new(config["Tenant:ConnectionString"]!,id)) : new UnavailableProfileDiagnostics();
});
builder.Services.TryAddSingleton<IScheduleCommands>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"]!="Memory" && Guid.TryParse(config["Tenant:InstanceId"],out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"])
        ? new SqlScheduleCommands(new(config["Tenant:ConnectionString"]!,id),provider.GetRequiredService<IScheduleEvaluator>(),provider.GetRequiredService<TimeProvider>())
        : new UnavailableScheduleCommands();
});
builder.Services.TryAddSingleton<SqlJobLifecycle>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    if(config["Tenant:Adapter"]=="Memory" || !Guid.TryParse(config["Tenant:InstanceId"],out var id) || string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"]))
        throw new WorkerDockException("PersistenceUnavailable");
    return new SqlJobLifecycle(new(config["Tenant:ConnectionString"]!,id),provider.GetRequiredService<ITemplateCatalog>(),provider.GetRequiredService<IScheduleEvaluator>(),provider.GetRequiredService<TimeProvider>());
});
builder.Services.TryAddSingleton<IJobLifecycle>(provider => provider.GetRequiredService<SqlJobLifecycle>());
builder.Services.TryAddSingleton<IJobDuplicator>(provider => provider.GetRequiredService<SqlJobLifecycle>());
builder.Services.TryAddSingleton<IMonitoringQueries>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var clock = provider.GetRequiredService<TimeProvider>();
    if (Guid.TryParse(config["Tenant:InstanceId"], out var id) && !string.IsNullOrWhiteSpace(config["Tenant:ConnectionString"]) && config["Tenant:Adapter"] != "Memory")
        return new SqlMonitoringQueries(new(config["Tenant:ConnectionString"]!, id), clock);
    return new UnavailableMonitoringQueries(Guid.TryParse(config["Tenant:InstanceId"], out var instanceId) ? instanceId : Guid.Empty, clock);
});
var app = builder.Build();
var access = app.Services.GetRequiredService<AccessOptions>();
if (!access.AuthenticationConfigured)
    app.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().RemoveScheme("oidc");
app.UseExceptionHandler(handler => handler.Run(context =>
    (context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error is BadHttpRequestException
        ? SafeProblem(400,"RequestInvalid") : SafeProblem(500,"RequestFailed")).ExecuteAsync(context)));
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseAuthentication();
app.UseWorkerDockSessions();
app.UseAuthorization();
app.MapWorkerDockSessions();
app.MapJobCommands();
app.MapExecutionControls();
app.MapCapacity();
app.MapTemplateCatalog();
app.MapConnectionProfiles();
app.MapIntegrationSettings();
app.MapSchedules();
app.MapJobLifecycle();
app.MapMonitoring();
app.MapHealthEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

static IResult SafeProblem(int status, string code, string field = "") =>
    Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code, ["field"] = field });
static string ETag(byte[] version) => '"' + Convert.ToBase64String(version) + '"';

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
internal sealed class UnavailableIntegrationSettings : IIntegrationSettings
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<WorkerDock.Application.Settings.VaultSettingsDto> GetVaultAsync(CancellationToken ct) => Unavailable<WorkerDock.Application.Settings.VaultSettingsDto>();
    public ValueTask<CommandReceipt> SaveVaultAsync(WorkerDock.Application.Settings.VaultSettingsDto settings, ActorContext actor, CancellationToken ct) => Unavailable<CommandReceipt>();
    public ValueTask<WorkerDock.Application.Settings.EgressPolicyDto> GetEgressAsync(string name, CancellationToken ct) => Unavailable<WorkerDock.Application.Settings.EgressPolicyDto>();
    public ValueTask<CommandReceipt> SaveEgressAsync(WorkerDock.Application.Settings.EgressPolicyDto policy, ActorContext actor, CancellationToken ct) => Unavailable<CommandReceipt>();
    public ValueTask<WorkerDock.Application.Settings.IntegrationConfigurationDto> GetSnapshotAsync(CancellationToken ct) => Unavailable<WorkerDock.Application.Settings.IntegrationConfigurationDto>();
}
internal sealed class UnavailableJobCommands : IJobCommands
{
    public ValueTask<CommandReceipt> SaveAsync(WorkerDock.Domain.Jobs.JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
        => ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> EnableManualAsync(Guid jobId,byte[] expectedVersion,ActorContext actor,CancellationToken ct)
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
internal sealed class UnavailableScheduleCommands : IScheduleCommands
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> SaveAsync(Guid jobId,WorkerDock.Domain.Scheduling.ScheduleDefinition definition,WorkerDock.Domain.Scheduling.MissedWorkPolicy policy,byte[]? expectedVersion,ActorContext actor,CancellationToken ct)=>Unavailable<CommandReceipt>();
    public ValueTask<WorkerDock.Application.Scheduling.ScheduleEditorDto> GetAsync(Guid jobId,CancellationToken ct)=>Unavailable<WorkerDock.Application.Scheduling.ScheduleEditorDto>();
    public ValueTask<CommandReceipt> DeleteAsync(Guid jobId,byte[] expectedVersion,ActorContext actor,CancellationToken ct)=>Unavailable<CommandReceipt>();
    public ValueTask<IReadOnlyList<DateTimeOffset>> PreviewAsync(WorkerDock.Domain.Scheduling.ScheduleDefinition definition,DateTimeOffset afterUtc,int count,CancellationToken ct)=>Unavailable<IReadOnlyList<DateTimeOffset>>();
}
internal sealed class UnavailableMonitoringQueries(Guid instanceId, TimeProvider clock) : IMonitoringQueries
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<DashboardDto> DashboardAsync(CancellationToken ct) => Unavailable<DashboardDto>();
    public ValueTask<Page<JobSummaryDto>> JobsAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct) => Unavailable<Page<JobSummaryDto>>();
    public ValueTask<ExecutionDetailsDto> ExecutionAsync(Guid executionId, CancellationToken ct) => Unavailable<ExecutionDetailsDto>();
    public ValueTask<Page<AuditSummaryDto>> AuditAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct) => Unavailable<Page<AuditSummaryDto>>();
    public ValueTask<IReadOnlyList<WorkerSummaryDto>> WorkersAsync(CancellationToken ct) => Unavailable<IReadOnlyList<WorkerSummaryDto>>();
    public ValueTask<TenantHealthDto> HealthAsync(CancellationToken ct) => ValueTask.FromResult(new TenantHealthDto(instanceId, "Unavailable", new Dictionary<string,string> { ["web"] = "Healthy", ["database"] = "Unavailable" }, clock.GetUtcNow()));
}
internal sealed class UnavailableTemplateCatalog : ITemplateCatalog
{
    public ValueTask<string> RegisterAsync(IReadOnlyList<WorkerDock.Contracts.Templates.TemplateDescriptor> descriptors,CancellationToken ct) => ValueTask.FromException<string>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<IReadOnlyList<WorkerDock.Contracts.Templates.TemplateDescriptor>> ListAsync(CancellationToken ct) => ValueTask.FromException<IReadOnlyList<WorkerDock.Contracts.Templates.TemplateDescriptor>>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<WorkerDock.Contracts.Templates.TemplateDescriptor?> GetAsync(string id,string version,CancellationToken ct) => ValueTask.FromException<WorkerDock.Contracts.Templates.TemplateDescriptor?>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<bool> IsSelectableAsync(string id,string version,CancellationToken ct) => ValueTask.FromException<bool>(new WorkerDockException("PersistenceUnavailable"));
}
