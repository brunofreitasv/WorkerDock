using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Connections;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Templates.SqlServer;
/// <summary>Internal connectivity-only handler, selected exclusively by the durable server-authored diagnostic marker.</summary>
internal static class SqlProfileDiagnostic
{
    internal static string SafeCode(int number)=>number switch { 18456 or 18452=>"SqlAuthenticationFailed",4060 or 911=>"SqlCatalogUnavailable",-2=>"SqlTimeout",_=>"SqlTargetUnavailable" };
    internal static async ValueTask<TemplateResult> ExecuteAsync(ConnectionProfileRevision profile,string catalog,ISecretVault vault,CancellationToken ct)
    {
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var connection=await SqlConnectionBuilder.BuildAsync(profile,catalog,vault,bounded.Token);
            await connection.OpenAsync(bounded.Token);
            await using var command=connection.CreateCommand();command.CommandText="SELECT DB_ID()";command.CommandTimeout=15;
            if(await command.ExecuteScalarAsync(bounded.Token) is not int) return SqlTemplate.Result("PermanentFailure","SqlCatalogUnavailable",false);
            return SqlTemplate.Result("Success","SqlConnectivityConfirmed",false);
        }
        catch(SqlException error) { return SqlTemplate.Result("PermanentFailure",SafeCode(error.Number),false); }
        catch(SqlTemplateException error) { return SqlTemplate.Result("PermanentFailure",error.SafeCode,false); }
        catch(SecretUnavailableException) { return SqlTemplate.Result("PermanentFailure","SecretUnavailable",false); }
        catch(OperationCanceledException) { return SqlTemplate.Result("PermanentFailure","SqlDiagnosticTimeout",false); }
        catch { return SqlTemplate.Result("PermanentFailure","SqlTargetUnavailable",false); }
    }
}
