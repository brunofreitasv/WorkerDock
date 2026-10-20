using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace WorkerDock.Infrastructure.Values;

/// <summary>Applies already validated pointers to a detached JSON configuration.</summary>
public static class DynamicValueTargets
{
    public static JsonElement Apply(JsonElement configuration, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (configuration.ValueKind == JsonValueKind.Undefined || values.Count > 128 ||
            Encoding.UTF8.GetByteCount(configuration.GetRawText()) + values.Sum(x => Encoding.UTF8.GetByteCount(x.Key) + Encoding.UTF8.GetByteCount(x.Value.GetRawText())) > 49_152)
            throw new ArgumentException("Invalid dynamic values input.");
        var root = JsonNode.Parse(configuration.GetRawText())!;
        foreach (var (pointer, value) in values)
        {
            if (!TrySegments(pointer, out var segments) || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
                throw new ArgumentException("Invalid dynamic values input.");
            JsonNode? current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                current = current switch
                {
                    JsonObject obj when obj.TryGetPropertyValue(segments[i], out var child) => child,
                    JsonArray array when Index(segments[i], array.Count, out var index) => array[index],
                    _ => null
                };
                if (current is null) throw new ArgumentException("Invalid dynamic values input.");
            }
            var last = segments[^1];
            var replacement = JsonNode.Parse(value.GetRawText());
            if (replacement is not JsonValue) throw new ArgumentException("Invalid dynamic values input.");
            switch (current)
            {
                case JsonObject obj when !obj.ContainsKey(last) || obj[last] is JsonValue: obj[last] = replacement; break;
                case JsonArray array when Index(last, array.Count, out var index) && array[index] is JsonValue: array[index] = replacement; break;
                default: throw new ArgumentException("Invalid dynamic values input.");
            }
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static bool TrySegments(string pointer, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/' || pointer.Length > 256) return false;
        var raw = pointer[1..].Split('/');
        var result = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var b = new System.Text.StringBuilder();
            for (var j = 0; j < raw[i].Length; j++)
            {
                if (raw[i][j] != '~') { b.Append(raw[i][j]); continue; }
                if (++j >= raw[i].Length || raw[i][j] is not ('0' or '1')) return false;
                b.Append(raw[i][j] == '0' ? '~' : '/');
            }
            result[i] = b.ToString();
        }
        segments = result;
        return true;
    }

    private static bool Index(string segment, int count, out int index)
    {
        index = 0;
        return segment.Length > 0 && (segment == "0" || segment[0] is >= '1' and <= '9') &&
            segment.All(char.IsAsciiDigit) && int.TryParse(segment, out index) && index < count;
    }
}
