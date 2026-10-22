using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Web;

public sealed class TenantSqlStartup
{
    private readonly IConfiguration config;
    private readonly ISecretVault vault;
    private TenantDatabaseOptions? options;

    public TenantSqlStartup(IConfiguration config, IHostEnvironment environment, ISecretVault vault)
    {
        ArgumentNullException.ThrowIfNull(environment);
        this.config = config;
        this.vault = vault;
    }

    public TenantDatabaseOptions Options => options ?? throw new InvalidOperationException("SqlConfigurationInvalid");

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        TenantSqlConfiguration.RejectLegacy(config, "Tenant:ConnectionString");
        if (config["Tenant:Adapter"] == "Memory") return;
        if (!Guid.TryParse(config["Tenant:InstanceId"], out var instanceId))
            throw new InvalidOperationException("SqlConfigurationInvalid");
        var settings = TenantSqlConfiguration.Read(config, "Tenant:Sql");
        var connection = await TenantSqlConnectionResolver.ResolveAsync(settings, vault, ct);
        options = new(connection, instanceId);
    }
}
