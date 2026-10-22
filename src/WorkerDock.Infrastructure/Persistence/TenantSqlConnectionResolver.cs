using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Infrastructure.Persistence;

public static class TenantSqlConnectionResolver
{
    public static async ValueTask<string> ResolveAsync(TenantSqlSettings settings, ISecretVault vault, CancellationToken ct)
    {
        TenantSqlConfiguration.Validate(settings);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var password = await vault.ResolveAsync(settings.PasswordReference, ct);
            ct.ThrowIfCancellationRequested();
            return TenantSqlConnectionBuilder.BuildWithPassword(
                settings.Host, settings.Port, settings.Database, settings.User, password.Reveal().ToString());
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
