using System.Net;
using System.Text.Json;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.ControlCenter.Health;

public interface ITenantHealthProbe
{
    ValueTask<TenantDirectoryEntry> CheckAsync(Guid registrationId, CancellationToken ct);
}

public sealed class TenantHealthProbe(ICentralRegistry registry, ISecretVault vault, IHttpClientFactory clients, TimeProvider clock) : ITenantHealthProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int MaxResponseBytes = 32 * 1024;

    public async ValueTask<TenantDirectoryEntry> CheckAsync(Guid registrationId, CancellationToken ct)
    {
        var registration = await registry.GetAsync(registrationId, ct) ?? throw new WorkerDockException("TenantRegistrationNotFound");
        var attemptedUtc = clock.GetUtcNow();
        string state = "Unknown";
        string? safeCode = null;
        DateTimeOffset? succeededUtc = null;

        try
        {
            using var secret = await vault.ResolveAsync(registration.HealthCredential, ct);
            var token = new string(secret.Reveal().Span);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(registration.DashboardUrl, "health/ready"));
            request.Headers.TryAddWithoutValidation("X-WorkerDock-Health", token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            using var response = await clients.CreateClient("central-health").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                state = "Unhealthy";
                safeCode = "HealthEndpointUnavailable";
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = await ReadBoundedAsync(stream, MaxResponseBytes, timeout.Token);
                var result = JsonSerializer.Deserialize<HealthReply>(bytes, JsonOptions);
                if (result is null || result.InstanceId == Guid.Empty)
                {
                    state = "Unhealthy";
                    safeCode = "InvalidHealthResponse";
                }
                else if (result.InstanceId != registration.ExpectedInstanceId)
                {
                    state = "IdentityMismatch";
                    safeCode = "IdentityMismatch";
                }
                else
                {
                    state = "Healthy";
                    succeededUtc = clock.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { state = "Unknown"; safeCode = "HealthProbeTimeout"; }
        catch (SecretUnavailableException) { state = "Unknown"; safeCode = "HealthCredentialUnavailable"; }
        catch (OperationCanceledException) { throw; }
        catch { state = "Unknown"; safeCode = "HealthProbeFailed"; }

        return await registry.RecordHealthAsync(registrationId, state, safeCode, attemptedUtc, succeededUtc, ct);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit) throw new InvalidDataException("HealthResponseTooLarge");
            output.Write(buffer, 0, read);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 8 };
    private sealed record HealthReply(Guid InstanceId, string? Overall, DateTimeOffset? ObservedUtc);
}
