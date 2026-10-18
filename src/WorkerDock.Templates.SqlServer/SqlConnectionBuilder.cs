using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Connections;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Templates.SqlServer;
public static class SqlConnectionBuilder
{
    public static async ValueTask<SqlConnection> BuildAsync(ConnectionProfileRevision profile,string catalog,ISecretVault vault,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (profile.AllowedCatalogs is null || !profile.AllowedCatalogs.Contains(catalog,StringComparer.OrdinalIgnoreCase)) throw new SqlTemplateException("CatalogNotAllowed");
        if (string.IsNullOrWhiteSpace(profile.Server) || profile.Server.Length>256 || string.IsNullOrWhiteSpace(catalog) || catalog.Length>128) throw new SqlTemplateException("ProfileInvalid");
        var builder=new SqlConnectionStringBuilder
        {
            DataSource=profile.Server,InitialCatalog=catalog,Encrypt=profile.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate=profile.TrustServerCertificate,PersistSecurityInfo=false,ConnectTimeout=15,ConnectRetryCount=0,
            MultipleActiveResultSets=false,ApplicationName="WorkerDock SQL template",Pooling=false,Enlist=false
        };
        if(profile.Authentication=="Integrated" && profile.UserName is null && profile.PasswordReference is null) builder.IntegratedSecurity=true;
        else if(profile.Authentication=="VaultPassword" && !string.IsNullOrWhiteSpace(profile.UserName) && profile.PasswordReference is not null)
        {
            using var password=await vault.ResolveAsync(profile.PasswordReference,ct);
            builder.IntegratedSecurity=false; builder.UserID=profile.UserName; builder.Password=new string(password.Reveal().Span);
        }
        else throw new SqlTemplateException("ProfileInvalid");
        return new SqlConnection(builder.ConnectionString);
    }
}
