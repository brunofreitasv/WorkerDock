using Microsoft.Data.SqlClient;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Infrastructure.Persistence;

public static class TenantSqlConnectionResolver
{
    public static async ValueTask<string> ResolveAsync(TenantSqlSettings settings, ISecretVault vault, CancellationToken ct)
    {
        TenantSqlConfiguration.Validate(settings);
        ct.ThrowIfCancellationRequested();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{settings.Host},{settings.Port}",
            InitialCatalog = settings.Database,
            UserID = settings.User,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false,
            PersistSecurityInfo = false
        };
        try
        {
            using var password = await vault.ResolveAsync(settings.PasswordReference, ct);
            ct.ThrowIfCancellationRequested();
            builder.Password = password.Reveal().ToString();
            return builder.ConnectionString;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch
        {
            throw new SecretUnavailableException();
        }
    }
}
