using Microsoft.Data.SqlClient;

namespace WorkerDock.IntegrationTests;

internal static class QuartzRuntimeConnection
{
    internal static SqlConnectionStringBuilder ForDatabase(string? configured, string ownerConnection)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL_RUNTIME with a distinct restricted SQL login.");
        try
        {
            var owner = new SqlConnectionStringBuilder(ownerConnection);
            var runtime = new SqlConnectionStringBuilder(configured);
            if (runtime.IntegratedSecurity || string.IsNullOrWhiteSpace(runtime.UserID) ||
                string.Equals(runtime.UserID, owner.UserID, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(runtime.DataSource, owner.DataSource, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(owner.InitialCatalog))
                throw new InvalidOperationException("SQL_TEST_BLOCKED: a separate SQL login on the disposable test server is required.");
            runtime.InitialCatalog = owner.InitialCatalog;
            runtime.ConnectTimeout = Math.Clamp(runtime.ConnectTimeout, 1, 10);
            return runtime;
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("SQL_TEST_BLOCKED: invalid WORKERDOCK_TEST_SQL_RUNTIME connection settings.");
        }
    }
}
