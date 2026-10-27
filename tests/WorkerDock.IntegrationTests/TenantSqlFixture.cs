using Microsoft.Data.SqlClient;
using System.Data;
using WorkerDock.Infrastructure.Migrations;

namespace WorkerDock.IntegrationTests;

public sealed class TenantSqlFixture : IAsyncDisposable
{
    public SqlDatabaseFixture Database { get; } = new();
    public MigrationRunner Runner => new(Database.ConnectionString, Database.InstanceId, Database.RuntimePrincipal, Database.ScriptsRoot);
    private readonly SqlConnectionStringBuilder runtime;
    public string RuntimeConnectionString => runtime.ConnectionString;

    public TenantSqlFixture() => runtime = TenantRuntimeConnection.ForDatabase(
        Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_RUNTIME"), Database.ConnectionString);

    public async Task CreateAsync(bool snapshot)
    {
        await Database.CreateAsync(snapshot, runtime.UserID);
        await Runner.ApplyAsync("tenant", "0001_initial", default);
    }

    public async ValueTask DisposeAsync() => await Database.DisposeAsync();
}
