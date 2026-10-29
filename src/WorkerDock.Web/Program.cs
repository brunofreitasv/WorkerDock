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
using WorkerDock.Web.Endpoints;
using WorkerDock.Templates.SqlServer;
using WorkerDock.Templates.Http;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Monitoring;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Application.Observability;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Application.Alerts;
using WorkerDock.Infrastructure.Alerts;
using WorkerDock.Application.Retention;
using WorkerDock.Infrastructure.Retention;
using WorkerDock.Infrastructure.Values;
using WorkerDock.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWorkerDockAccess();
builder.Services.TryAddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.TryAddSingleton<ISecretVault>(provider => SecretVaultRegistration.CreateTenantSql(provider.GetRequiredService<IHostEnvironment>().IsDevelopment()));
builder.Services.TryAddSingleton<TenantSqlStartup>();
builder.Services.TryAddSingleton<IScheduleEvaluator, FiveFieldScheduleEvaluator>();
var trustedTemplates = new WorkerDock.Contracts.Templates.TemplateDescriptor[]
{
    new SqlTemplate("1.1.0").Descriptor,
    HttpTemplateDescriptors.Get("1.1.0")
};
builder.Services.TryAddSingleton<ITemplateAuthoringService>(_ => BuiltInAuthoringValidators.Create());
builder.Services.TryAddSingleton<DynamicValueResolver>();
builder.Services.TryAddSingleton<ExecutionValuePreparation>();
builder.Services.TryAddSingleton<IJobCommands>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (!Guid.TryParse(config["Tenant:InstanceId"], out var instanceId)) return new UnavailableJobCommands();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryJobCommands(provider.GetRequiredService<TimeProvider>(),profiles:provider.GetRequiredService<IConnectionProfiles>(),integrationSettings:provider.GetRequiredService<IIntegrationSettings>(),templateCatalog:provider.GetRequiredService<ITemplateCatalog>(),authoring:provider.GetRequiredService<ITemplateAuthoringService>()) : new UnavailableJobCommands();
    return new SqlJobCommands(provider.GetRequiredService<TenantSqlStartup>().Options,provider.GetRequiredService<ITemplateCatalog>(),provider.GetRequiredService<ITemplateAuthoringService>());
});
builder.Services.TryAddSingleton<IIntegrationSettings>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryIntegrationSettings() : new UnavailableIntegrationSettings();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlIntegrationSettings(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableIntegrationSettings();
});
builder.Services.TryAddSingleton<ITenantSettingsService>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (!Guid.TryParse(config["Tenant:InstanceId"], out var instanceId)) return new UnavailableTenantSettingsService();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryTenantSettingsService(instanceId) : new UnavailableTenantSettingsService();
    return new TenantSettingsService(provider.GetRequiredService<TenantSqlStartup>().Options);
});
builder.Services.TryAddSingleton<IExecutionControls>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    if (config["Tenant:Adapter"] == "Memory")
        return provider.GetRequiredService<IHostEnvironment>().IsDevelopment() && provider.GetRequiredService<IJobCommands>() is IExecutionControls controls
            ? controls : new UnavailableExecutionControls();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlExecutionControls(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableExecutionControls();
});
builder.Services.TryAddSingleton<ICapacityCommands>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlCapacityCommands(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableCapacityCommands();
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
    return config["Tenant:Adapter"]!="Memory"
        ? new SqlTemplateCatalog(provider.GetRequiredService<TenantSqlStartup>().Options,trustedTemplates) : new UnavailableTemplateCatalog();
});
builder.Services.TryAddSingleton<IConnectionProfiles>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    if(config["Tenant:Adapter"]=="Memory" && provider.GetRequiredService<IHostEnvironment>().IsDevelopment())return new InMemoryConnectionProfiles();
    return config["Tenant:Adapter"]!="Memory"
        ? new SqlConnectionProfiles(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableConnectionProfiles();
});
builder.Services.TryAddSingleton<IProfileDiagnostics>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"]!="Memory"
        ? new SqlProfileDiagnostics(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableProfileDiagnostics();
});
builder.Services.TryAddSingleton<IScheduleCommands>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"]!="Memory"
        ? new SqlScheduleCommands(provider.GetRequiredService<TenantSqlStartup>().Options,provider.GetRequiredService<IScheduleEvaluator>(),provider.GetRequiredService<TimeProvider>())
        : new UnavailableScheduleCommands();
});
builder.Services.TryAddSingleton<SqlJobLifecycle>(provider =>
{
    var config=provider.GetRequiredService<IConfiguration>();
    if(config["Tenant:Adapter"]=="Memory")
        throw new WorkerDockException("PersistenceUnavailable");
    return new SqlJobLifecycle(provider.GetRequiredService<TenantSqlStartup>().Options,provider.GetRequiredService<ITemplateCatalog>(),provider.GetRequiredService<IScheduleEvaluator>(),provider.GetRequiredService<TimeProvider>(),provider.GetRequiredService<ITemplateAuthoringService>());
});
builder.Services.TryAddSingleton<IJobLifecycle>(provider => provider.GetRequiredService<SqlJobLifecycle>());
builder.Services.TryAddSingleton<IJobDuplicator>(provider => provider.GetRequiredService<SqlJobLifecycle>());
builder.Services.TryAddSingleton<IMonitoringQueries>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var clock = provider.GetRequiredService<TimeProvider>();
    if (config["Tenant:Adapter"] != "Memory")
        return new SqlMonitoringQueries(provider.GetRequiredService<TenantSqlStartup>().Options, clock);
    return new UnavailableMonitoringQueries(Guid.TryParse(config["Tenant:InstanceId"], out var instanceId) ? instanceId : Guid.Empty, clock);
});
builder.Services.TryAddSingleton<IExecutionEventQueries>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlExecutionEventQueries(provider.GetRequiredService<TenantSqlStartup>().Options)
        : new UnavailableExecutionEventQueries();
});
builder.Services.TryAddSingleton<UnavailableAlertConfigurations>();
builder.Services.TryAddSingleton<IAlertRules>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlAlertRules(provider.GetRequiredService<TenantSqlStartup>().Options) : provider.GetRequiredService<UnavailableAlertConfigurations>();
});
builder.Services.TryAddSingleton<IAlertSinks>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlAlertRules(provider.GetRequiredService<TenantSqlStartup>().Options) : provider.GetRequiredService<UnavailableAlertConfigurations>();
});
builder.Services.TryAddSingleton<IAlertQueries>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlAlertQueries(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableAlertQueries();
});
builder.Services.TryAddSingleton<IAlertDeliveryStore>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlAlertDeliveryStore(provider.GetRequiredService<TenantSqlStartup>().Options) : new UnavailableAlertDeliveryStore();
});
builder.Services.TryAddSingleton<IRetentionService>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (config["Tenant:Adapter"] == "Memory")
        return environment.IsDevelopment() ? new InMemoryRetentionService() : new UnavailableRetentionService();
    return config["Tenant:Adapter"] != "Memory"
        ? new SqlRetentionService(provider.GetRequiredService<TenantSqlStartup>().Options, config["Retention:ArchiveRoot"])
        : new UnavailableRetentionService();
});
var app = builder.Build();
await app.Services.GetRequiredService<TenantSqlStartup>().InitializeAsync(app.Lifetime.ApplicationStopping);
if (app.Configuration["Tenant:Adapter"] != "Memory")
    await app.Services.GetRequiredService<ITemplateCatalog>().RegisterAsync(trustedTemplates, app.Lifetime.ApplicationStopping);
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
app.MapDynamicValuePreview();
app.MapConnectionProfiles();
app.MapIntegrationSettings();
app.MapSchedules();
app.MapJobLifecycle();
app.MapMonitoring();
app.MapExecutionEventEndpoints();
app.MapAlerts();
app.MapRetention();
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
}).RequireAuthorization(AccessPolicies.Read);

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
await app.RunAsync();
public partial class Program;
internal sealed class UnavailableAlertQueries : IAlertQueries
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<IReadOnlyList<AlertIncidentDto>> ListIncidentsAsync(int limit, CancellationToken ct) => Unavailable<IReadOnlyList<AlertIncidentDto>>();
    public ValueTask<IReadOnlyList<AlertDeliveryDto>> ListDeliveriesAsync(int limit, CancellationToken ct) => Unavailable<IReadOnlyList<AlertDeliveryDto>>();
}
internal sealed class UnavailableAlertDeliveryStore : IAlertDeliveryStore
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<IReadOnlyList<DueAlertDelivery>> ReadDueAsync(int limit, CancellationToken ct) => Unavailable<IReadOnlyList<DueAlertDelivery>>();
    public ValueTask<AlertDeliveryClaim?> TryBeginAsync(Guid deliveryId, int expectedAttemptOrdinal, Guid ownerBootId, CancellationToken ct) => Unavailable<AlertDeliveryClaim?>();
    public ValueTask<bool> RenewAsync(AlertDeliveryLease lease, CancellationToken ct) => Unavailable<bool>();
    public ValueTask<bool> CompleteAsync(AlertDeliveryLease lease, WorkerDock.Contracts.Alerts.AlertDeliveryResult result, CancellationToken ct) => Unavailable<bool>();
    public ValueTask<CommandReceipt> RetryFailedAsync(Guid deliveryId, int additionalAttempts, ActorContext actor, CancellationToken ct) => Unavailable<CommandReceipt>();
}
internal sealed class UnavailableAlertConfigurations : IAlertRules, IAlertSinks
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    ValueTask<IReadOnlyList<AlertRuleEditorDto>> IAlertRules.ListAsync(CancellationToken ct) => Unavailable<IReadOnlyList<AlertRuleEditorDto>>();
    ValueTask<AlertRuleEditorDto> IAlertRules.GetAsync(Guid id, CancellationToken ct) => Unavailable<AlertRuleEditorDto>();
    ValueTask<CommandReceipt> IAlertRules.SaveAsync(WorkerDock.Domain.Alerts.AlertRule rule, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) => Unavailable<CommandReceipt>();
    ValueTask<IReadOnlyList<AlertSinkEditorDto>> IAlertSinks.ListAsync(CancellationToken ct) => Unavailable<IReadOnlyList<AlertSinkEditorDto>>();
    ValueTask<AlertSinkEditorDto> IAlertSinks.GetAsync(Guid id, CancellationToken ct) => Unavailable<AlertSinkEditorDto>();
    ValueTask<CommandReceipt> IAlertSinks.SaveAsync(AlertSinkDefinition sink, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) => Unavailable<CommandReceipt>();
}
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
    public ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ManualRunOptions options, ActorContext actor, CancellationToken ct)
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
    public ValueTask<RuntimeStatusDto> WorkersAsync(CancellationToken ct) => Unavailable<RuntimeStatusDto>();
    public ValueTask<TenantHealthDto> HealthAsync(CancellationToken ct) => ValueTask.FromResult(new TenantHealthDto(instanceId, "Unavailable", new Dictionary<string,string> { ["web"] = "Healthy", ["database"] = "Unavailable" }, clock.GetUtcNow()));
}
internal sealed class UnavailableExecutionEventQueries : IExecutionEventQueries
{
    public ValueTask<ExecutionEventPage> ReadAsync(Guid executionId, string? cursor, int limit, CancellationToken ct) =>
        ValueTask.FromException<ExecutionEventPage>(new WorkerDockException("PersistenceUnavailable"));
}
internal sealed class UnavailableRetentionService : IRetentionService
{
    private static ValueTask<T> Unavailable<T>() => ValueTask.FromException<T>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<int> RunBatchAsync(DateTimeOffset nowUtc, int maxRows, CancellationToken ct) => Unavailable<int>();
    public ValueTask<RetentionPolicyEditorDto> GetPolicyAsync(CancellationToken ct) => Unavailable<RetentionPolicyEditorDto>();
    public ValueTask<CommandReceipt> SavePolicyAsync(RetentionPolicy policy, byte[] expectedVersion, ActorContext actor, CancellationToken ct) => Unavailable<CommandReceipt>();
}
internal sealed class UnavailableTemplateCatalog : ITemplateCatalog
{
    public ValueTask<string> RegisterAsync(IReadOnlyList<WorkerDock.Contracts.Templates.TemplateDescriptor> descriptors,CancellationToken ct) => ValueTask.FromException<string>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<IReadOnlyList<WorkerDock.Contracts.Templates.TemplateDescriptor>> ListAsync(CancellationToken ct) => ValueTask.FromException<IReadOnlyList<WorkerDock.Contracts.Templates.TemplateDescriptor>>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<WorkerDock.Contracts.Templates.TemplateDescriptor?> GetAsync(string id,string version,CancellationToken ct) => ValueTask.FromException<WorkerDock.Contracts.Templates.TemplateDescriptor?>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<bool> IsSelectableAsync(string id,string version,CancellationToken ct) => ValueTask.FromException<bool>(new WorkerDockException("PersistenceUnavailable"));
}
