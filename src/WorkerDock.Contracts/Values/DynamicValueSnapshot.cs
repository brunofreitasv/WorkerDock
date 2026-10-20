using System.Text.Json;

namespace WorkerDock.Contracts.Values;

public sealed record DynamicValueSnapshot(int ResolverVersion, string InputDigest,
    IReadOnlyDictionary<string, JsonElement> Values);

public sealed class ValueResolutionException(string code, string target) : Exception(code)
{
    public string Code { get; } = code;
    public string Target { get; } = target;
}
