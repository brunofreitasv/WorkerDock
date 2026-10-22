using Microsoft.Data.SqlClient;

namespace WorkerDock.Infrastructure.Persistence;

public static class TenantSqlConnectionBuilder
{
    public static string BuildWithPassword(string host, int port, string database, string user, string password)
    {
        TenantSqlConfiguration.ValidateEndpoint(host, port, database, user);
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("SqlConfigurationInvalid");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{host},{port}",
            InitialCatalog = database,
            UserID = user,
            Password = password,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false,
            PersistSecurityInfo = false
        };
        return builder.ConnectionString;
    }
}
