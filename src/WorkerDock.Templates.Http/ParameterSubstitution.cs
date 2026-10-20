using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkerDock.Templates.Http;

public static partial class ParameterSubstitution
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static string Apply(string template, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey)
        => ApplyCore(template, parameters, idempotencyKey, encode: true);

    public static string ApplyUrl(string text, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey) =>
        ApplyCore(text, parameters, idempotencyKey, encode: true);

    public static string ApplyHeader(string text, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey) =>
        ApplyCore(text, parameters, idempotencyKey, encode: false);

    public static string ApplyText(string text, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey) =>
        ApplyCore(text, parameters, idempotencyKey, encode: false);

    private static string ApplyCore(string template, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey, bool encode)
    {
        if (template is null || template.Length > 65_536 || parameters.Count > 128 || idempotencyKey.Length > 256)
            throw new InvalidOperationException("HttpConfigurationInvalid");
        try
        {
            return Placeholder().Replace(template, match =>
            {
                var key = match.Groups[1].Value;
                if (key == "execution.idempotencyKey") return encode ? Uri.EscapeDataString(idempotencyKey) : idempotencyKey;
                if (!parameters.TryGetValue(key, out var value)) throw new InvalidOperationException("HttpParameterMissing");
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    JsonValueKind.Null => string.Empty,
                    _ => throw new InvalidOperationException("HttpParameterInvalid")
                };
                return encode ? Uri.EscapeDataString(text) : text;
            });
        }
        catch (RegexMatchTimeoutException) { throw new InvalidOperationException("HttpConfigurationInvalid"); }
    }

    public static string ApplyJson(string template, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey)
    {
        if (template is null || template.Length > 65_536) throw new InvalidOperationException("HttpConfigurationInvalid");
        var result = Placeholder().Replace(template, match =>
        {
            var value = Resolve(match.Groups[1].Value, parameters, idempotencyKey);
            var json = value;
            return InsideJsonString(template, match.Index) ? json.Length >= 2 && json[0] == '"' && json[^1] == '"' ? json[1..^1] : json : json;
        });
        try { using var _ = JsonDocument.Parse(result); }
        catch { throw new InvalidOperationException("HttpConfigurationInvalid"); }
        return result;
    }

    private static string Resolve(string key, IReadOnlyDictionary<string, JsonElement> parameters, string idempotencyKey)
    {
        if (key == "execution.idempotencyKey") return JsonSerializer.Serialize(idempotencyKey);
        if (!parameters.TryGetValue(key, out var value)) throw new InvalidOperationException("HttpParameterMissing");
        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined) throw new InvalidOperationException("HttpParameterInvalid");
        return value.GetRawText();
    }

    private static bool InsideJsonString(string text, int position)
    {
        var inside = false;
        for (var i = 0; i < position; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') inside = !inside;
        }
        return inside;
    }

    [GeneratedRegex("\\{([A-Za-z][A-Za-z0-9_.-]{0,63})\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
