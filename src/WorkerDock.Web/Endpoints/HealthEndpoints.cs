using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using WorkerDock.Application.Monitoring;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Web.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Json(new { status = "live" }));
        endpoints.MapGet("/health/ready", async (HttpRequest request, IConfiguration config, ISecretVault vault, IMonitoringQueries queries, CancellationToken ct) =>
        {
            if (!await AuthorizedAsync(request, config["Health:CredentialReference"], vault, ct)) return Results.Unauthorized();
            var health = await queries.HealthAsync(ct);
            return Results.Json(health, statusCode: health.Overall == "Healthy" ? 200 : 503);
        });
    }

    private static async ValueTask<bool> AuthorizedAsync(HttpRequest request, string? reference, ISecretVault vault, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return true;
        var supplied = request.Headers["X-WorkerDock-Health"].ToString();
        if (supplied.Length is < 1 or > 4096) return false;
        try
        {
            using var secret = await vault.ResolveAsync(new SecretReference(reference), ct);
            var expectedBytes = Encoding.UTF8.GetBytes(secret.Reveal().ToString());
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            try { return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes); }
            finally { CryptographicOperations.ZeroMemory(expectedBytes); CryptographicOperations.ZeroMemory(suppliedBytes); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
