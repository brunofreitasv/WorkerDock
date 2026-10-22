using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Secrets;
using WorkerDock.DatabaseMigrator;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Secrets;

if (!((args.Length == 3 && args[0] is "apply" or "rollback") ||
      (args.Length == 4 && args[0] == "prepare-upgrade")) ||
    !Guid.TryParse(Environment.GetEnvironmentVariable("WORKERDOCK_INSTANCE_ID"), out var instanceId))
{
    Console.Error.WriteLine("Usage: apply|rollback tenant <migration> or prepare-upgrade tenant <target-migration> <backup-path>; configure Migrator:Sql and WORKERDOCK_INSTANCE_ID.");
    return 2;
}

try
{
    // Enumerate keys because an empty environment value is still legacy configuration.
    if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key => key.Equals("WORKERDOCK_SQL", StringComparison.OrdinalIgnoreCase)))
        throw new WorkerDockException("LegacyConnectionStringUnsupported");
    var configuration = new ConfigurationBuilder();
    var path = Environment.GetEnvironmentVariable("WORKERDOCK_MIGRATOR_CONFIG_PATH");
    if (path is not null) configuration.AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false);
    var config = configuration.AddEnvironmentVariables().Build();
    using var configurationLifetime = config as IDisposable;
    var settings = TenantSqlConfiguration.Read(config, "Migrator:Sql");
    var services = new ServiceCollection();
    services.TryAddSingleton<ISecretVault>(_ => SecretVaultRegistration.CreateTenantSql(
        string.Equals(config["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase)));
    await using var provider = services.BuildServiceProvider();
    var connection = await TenantSqlConnectionResolver.ResolveAsync(settings, provider.GetRequiredService<ISecretVault>(), default);
    var runner = new MigrationRunner(connection, instanceId, Path.Combine(AppContext.BaseDirectory, "database"));
    if (args[0] == "prepare-upgrade") await runner.PrepareUpgradeAsync(args[1], args[2], args[3], default);
    else if (args[0] == "apply") await runner.ApplyAsync(args[1], args[2], default);
    else await runner.RollbackAsync(args[1], args[2], default);
    return 0;
}
catch (WorkerDockException error)
{
    Console.Error.WriteLine(error.Code);
    return 1;
}
catch (SecretUnavailableException)
{
    Console.Error.WriteLine("SecretUnavailable");
    return 1;
}
catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or
    UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
{
    Console.Error.WriteLine("SqlConfigurationInvalid");
    return 1;
}
