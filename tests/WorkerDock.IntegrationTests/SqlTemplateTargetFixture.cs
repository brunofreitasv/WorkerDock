using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Connections;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.IntegrationTests;
internal sealed class SqlTemplateTargetFixture : IAsyncDisposable
{
    internal SqlDatabaseFixture Database { get; }=new();
    private readonly SqlConnectionStringBuilder target;
    private bool installed;
    internal ConnectionProfileRevision Profile { get; }
    internal ISecretVault Vault => new TargetVault(target.Password);
    internal SqlTemplateTargetFixture()
    {
        var configured=Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_TARGET_RUNTIME");
        if(string.IsNullOrWhiteSpace(configured))throw new InvalidOperationException("SQL_TEST_BLOCKED: configure separate least-privilege WORKERDOCK_TEST_SQL_TARGET_RUNTIME SQL-authentication login.");
        target=new(configured) { InitialCatalog=new SqlConnectionStringBuilder(Database.ConnectionString).InitialCatalog,ConnectTimeout=5 };
        if(target.IntegratedSecurity || string.IsNullOrWhiteSpace(target.UserID) || string.IsNullOrWhiteSpace(target.Password))throw new InvalidOperationException("SQL_TEST_BLOCKED: target fixture requires SQL authentication and a separate restricted login.");
        var owner=new SqlConnectionStringBuilder(Database.ConnectionString);
        var runtime=Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL_RUNTIME");
        if(!string.Equals(target.DataSource,owner.DataSource,StringComparison.OrdinalIgnoreCase) || string.Equals(target.UserID,owner.UserID,StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(runtime) && string.Equals(target.UserID,new SqlConnectionStringBuilder(runtime).UserID,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL_TEST_BLOCKED: target must use a distinct login on the disposable test server.");
        Profile=SqlTemplateTests.Profile() with { Server=target.DataSource,AllowedCatalogs=[target.InitialCatalog],Authentication="VaultPassword",UserName=target.UserID,PasswordReference=new("target-password"),Encrypt=target.Encrypt!=SqlConnectionEncryptOption.Optional,TrustServerCertificate=target.TrustServerCertificate };
    }
    internal async Task CreateAsync()
    {
        await Database.CreateAsync(false);await ScriptAsync("up");installed=true;
        await using var c=new SqlConnection(target.ConnectionString);await c.OpenAsync();await using var command=c.CreateCommand();
        command.CommandText="SELECT CASE WHEN IS_MEMBER('db_owner')=0 AND IS_SRVROLEMEMBER('sysadmin')=0 AND HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE')=0 THEN 1 ELSE 0 END";
        Assert.Equal(1,Convert.ToInt32(await command.ExecuteScalarAsync()));
    }
    private async Task ScriptAsync(string direction)
    {
        var sql=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"target-sql","target-fixture."+direction+".sql"));
        await using var c=new SqlConnection(Database.ConnectionString);await c.OpenAsync();
        foreach(var batch in Regex.Split(sql,@"(?im)^\s*GO\s*$").Where(x=>!string.IsNullOrWhiteSpace(x)))
        {
            await using var command=c.CreateCommand();command.CommandText=batch;command.Parameters.Add(new SqlParameter("@TargetLogin",SqlDbType.NVarChar,128){Value=target.UserID});await command.ExecuteNonQueryAsync();
        }
    }
    public async ValueTask DisposeAsync()
    {
        try{if(installed)await ScriptAsync("down");}finally{await Database.DisposeAsync();}
    }
    private sealed class TargetVault(string password) : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();if(reference.Name!="target-password")throw new SecretUnavailableException();return ValueTask.FromResult(new SecretValue(password));}
    }
}
