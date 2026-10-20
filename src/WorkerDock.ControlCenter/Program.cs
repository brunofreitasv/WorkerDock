using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Application.Outbound;
using WorkerDock.ControlCenter;
using WorkerDock.ControlCenter.Endpoints;
using WorkerDock.ControlCenter.Health;
using WorkerDock.ControlCenter.Registry;
using WorkerDock.Infrastructure.Authentication;
using WorkerDock.Infrastructure.Outbound;
using WorkerDock.Infrastructure.Secrets;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["ControlCenter:InstanceId"] ??= builder.Configuration["Central:InstanceId"];
builder.Configuration["Access:CookiePrefix"] = "WorkerDock.Center";
builder.Services.AddOptions<CentralHealthOptions>()
    .BindConfiguration("Central:Health")
    .Validate(options => options.AllowedHosts.Length > 0 && options.AllowedHosts.All(host => !string.IsNullOrWhiteSpace(host)), "CentralHealthHostAllowlistRequired")
    .ValidateOnStart();
builder.Services.AddWorkerDockAccess("WorkerDock.Center");
builder.Services.AddAntiforgery();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.TryAddSingleton<ISecretVault>(provider => provider.GetRequiredService<IHostEnvironment>().IsDevelopment()
    ? SecretVaultRegistration.CreateDevelopment()
    : throw new InvalidOperationException("SecretVaultAdapterRequired"));
builder.Services.AddSingleton<IOutboundPolicy>(provider =>
{
    var health = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CentralHealthOptions>>().Value;
    return new OutboundPolicy(rules: new Dictionary<string, OutboundPolicyRule>(StringComparer.Ordinal)
    {
        ["central-health"] = new(health.AllowedHosts.ToHashSet(StringComparer.Ordinal), new HashSet<int> { 443 }, false, health.AllowedPrivateCidrs)
    });
});
builder.Services.AddSingleton<ValidatedConnectionFactory>();
builder.Services.AddHttpClient("central-health")
    .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ValidatedConnectionFactory>().CreateHandler("central-health"));
builder.Services.AddSingleton<ITenantHealthProbe, TenantHealthProbe>();
builder.Services.AddRateLimiter(options => options.AddPolicy("central-health-refresh", context =>
    RateLimitPartition.GetFixedWindowLimiter("all", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    })));
builder.Services.AddSingleton<ICentralRegistry>(provider =>
{
    var config = provider.GetRequiredService<IConfiguration>();
    var environment = provider.GetRequiredService<IHostEnvironment>();
    if (!Guid.TryParse(config["Central:InstanceId"], out var instanceId) || instanceId == Guid.Empty)
        throw new InvalidOperationException("CentralInstanceIdInvalid");
    var adapter = config["Central:Adapter"];
    if (string.Equals(adapter, "Memory", StringComparison.OrdinalIgnoreCase))
        return environment.IsDevelopment()
            ? new InMemoryCentralRegistry(provider.GetRequiredService<TimeProvider>())
            : throw new InvalidOperationException("CentralMemoryAdapterDevelopmentOnly");
    if (!string.IsNullOrWhiteSpace(adapter) && !string.Equals(adapter, "File", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("CentralStorageAdapterUnsupported");
    if (!string.IsNullOrWhiteSpace(config["Central:ConnectionString"]))
        throw new InvalidOperationException("CentralSqlConfigurationUnsupported");
    var dataDirectory = CentralStorageOptions.ValidateDataDirectory(config["Central:Storage:DataDirectory"], environment.ContentRootPath);
    return FileCentralRegistry.Open(dataDirectory, instanceId, provider.GetRequiredService<TimeProvider>());
});

var app = builder.Build();
// Open and validate persistent state before serving requests so configuration and journal failures
// prevent a misleading healthy process from starting.
_ = app.Services.GetRequiredService<ICentralRegistry>();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseWorkerDockSessions();
app.UseAuthorization();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWorkerDockSessions();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/health/ready", async (ICentralRegistry registry, CancellationToken ct) =>
{
    try
    {
        await registry.ListAsync(null, new(null, 1), ct);
        return Results.Ok(new { status = "Healthy" });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch { return Results.Json(new { status = "Unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable); }
});
app.MapRegistry();
app.MapFallbackToFile("index.html");
app.Run();
