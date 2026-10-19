using System.Text.Json;
using System.Text;
using WorkerDock.Application.Common;

namespace WorkerDock.Application.Alerts;

public sealed record WebhookHeader(string Name, string? Value, string? SecretReference);
public sealed record WebhookConfiguration(Uri Url, IReadOnlyList<WebhookHeader> Headers);

public static class AlertSinkValidation
{
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Proxy-Connection", "Upgrade", "TE", "Trailer", "Expect"
    };

    public static AlertSinkDefinition ValidateAndFreeze(AlertSinkDefinition sink)
    {
        if (sink is null || sink.Id == Guid.Empty || string.IsNullOrWhiteSpace(sink.Name) || sink.Name.Length > 150 ||
            sink.Name.Any(char.IsControl) || sink.Type != "webhook" || sink.MaxAttempts is < 1 or > 20 ||
            sink.RetryBaseDelay < TimeSpan.Zero || sink.RetryMaxDelay < sink.RetryBaseDelay || sink.RetryMaxDelay > TimeSpan.FromDays(1) ||
            sink.Timeout <= TimeSpan.Zero || sink.Timeout > TimeSpan.FromMinutes(2) ||
            sink.RetryBaseDelay.Ticks % TimeSpan.TicksPerSecond != 0 || sink.RetryMaxDelay.Ticks % TimeSpan.TicksPerSecond != 0 ||
            sink.Timeout.Ticks % TimeSpan.TicksPerSecond != 0 || sink.Configuration.ValueKind != JsonValueKind.Object)
            throw new WorkerDockException("ValidationFailed", "sink");

        try
        {
            if (Encoding.UTF8.GetByteCount(sink.Configuration.GetRawText()) > 4000 ||
                sink.Configuration.EnumerateObject().Any(property => property.Name is not ("url" or "headers")) ||
                !Uri.TryCreate(sink.Configuration.GetProperty("url").GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                throw new WorkerDockException("ValidationFailed", "configuration");

            var headers = new List<WebhookHeader>();
            if (sink.Configuration.TryGetProperty("headers", out var rawHeaders))
            {
                if (rawHeaders.ValueKind != JsonValueKind.Array || rawHeaders.GetArrayLength() > 32)
                    throw new WorkerDockException("ValidationFailed", "headers");
                foreach (var raw in rawHeaders.EnumerateArray())
                {
                    if (raw.ValueKind != JsonValueKind.Object || raw.EnumerateObject().Any(property => property.Name is not ("name" or "value" or "secretReference")))
                        throw new WorkerDockException("ValidationFailed", "headers");
                    var name = raw.GetProperty("name").GetString();
                    var value = raw.TryGetProperty("value", out var rawValue) ? rawValue.GetString() : null;
                    var reference = raw.TryGetProperty("secretReference", out var rawReference) ? rawReference.GetString() : null;
                    using var request = new HttpRequestMessage();
                    var validName = name is not null && request.Headers.TryAddWithoutValidation(name, "x");
                    if (!validName || name!.Length > 100 || TransportHeaders.Contains(name) || value is not null && (value.Length > 1000 || value.Any(char.IsControl)) ||
                        reference is not null && (string.IsNullOrWhiteSpace(reference) || reference.Length > 200 || reference.Any(char.IsControl)) ||
                        value is not null || reference is null)
                        throw new WorkerDockException("ValidationFailed", "headers");
                    headers.Add(new(name, value, reference));
                }
            }
            if (headers.Select(header => header.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count)
                throw new WorkerDockException("ValidationFailed", "headers");
            return sink with { Configuration = sink.Configuration.Clone() };
        }
        catch (WorkerDockException) { throw; }
        catch { throw new WorkerDockException("ValidationFailed", "configuration"); }
    }
}
