using System.Text.Json;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Templates.Http;

public sealed record HttpJobConfiguration(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, SecretReference> SecretHeaders,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    string? ContentType,
    string? Body,
    int? TimeoutSeconds,
    IReadOnlyList<int> SuccessStatuses,
    string? IdempotencyHeader,
    int MaxResponseBytes,
    string ResponseMode);

public static class HttpJobConfigurationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };
}
