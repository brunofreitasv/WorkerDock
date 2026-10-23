using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Monitoring;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Monitoring;
using WorkerDock.Infrastructure.Migrations;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Runtime;

namespace WorkerDock.IntegrationTests;

public sealed class TenantSqlWebStartupTests : IDisposable
{
    private readonly List<string> _keyDirectories = [];

    [Fact]
    [Trait("Store", "Sql")]
    public async Task Runtime_heartbeat_drives_ready_health()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", "0001_initial", default);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var status = new SqlRuntimeStatus(options, 2);
        var boot = Guid.NewGuid();

        await status.HeartbeatAsync(boot, true, default);
        Assert.Equal("Healthy", (await new SqlMonitoringQueries(options, TimeProvider.System).HealthAsync(default)).Overall);
        await status.HeartbeatAsync(boot, false, default);
        Assert.NotEqual("Healthy", (await new SqlMonitoringQueries(options, TimeProvider.System).HealthAsync(default)).Overall);
    }

    [Fact]
    public async Task Production_sql_uses_registered_vault_before_constructing_sql_services()
    {
        await using var factory = Host("Production", new()
        {
            ["Tenant:Adapter"] = "Sql",
            ["Tenant:InstanceId"] = Guid.NewGuid().ToString(),
            ["Tenant:Sql:Host"] = "sql.example.test",
            ["Tenant:Sql:Port"] = "1433",
            ["Tenant:Sql:Database"] = "Tenant",
            ["Tenant:Sql:User"] = "TenantUser",
            ["Tenant:Sql:PasswordReference"] = "tenant.database.password"
        }).WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<ISecretVault>(new TenantTestVault())));
        using var client = factory.CreateClient();
        Assert.IsType<SqlJobCommands>(factory.Services.GetRequiredService<IJobCommands>());
        Assert.IsType<SqlMonitoringQueries>(factory.Services.GetRequiredService<IMonitoringQueries>());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Sql_without_database_fields_fails_before_serving(string environment)
    {
        await using var factory = Host(environment, new()
        {
            ["Tenant:Adapter"] = "Sql",
            ["Tenant:InstanceId"] = Guid.NewGuid().ToString()
        });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Equal("SqlConfigurationInvalid", error.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Memory_rejects_even_an_empty_legacy_connection_string(string environment)
    {
        await using var factory = Host(environment, new()
        {
            ["Tenant:Adapter"] = "Memory",
            ["Tenant:InstanceId"] = Guid.NewGuid().ToString(),
            ["Tenant:ConnectionString"] = ""
        });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Equal("LegacyConnectionStringUnsupported", error.Message);
    }

    private static WebApplicationFactory<WorkerDock.Web.WebAssemblyMarker> Host(string environment, Dictionary<string, string?> settings)
    {
        settings["DataProtection:KeyDirectory"] = Path.Combine(Path.GetTempPath(), "WorkerDockSqlKeys_" + Guid.NewGuid().ToString("N"));
        return new WebApplicationFactory<WorkerDock.Web.WebAssemblyMarker>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(environment).ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));
    }

    private sealed class TenantTestVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reference.Name != "tenant.database.password") throw new SecretUnavailableException();
            return ValueTask.FromResult(new SecretValue("synthetic-tenant-password"));
        }
    }
}
