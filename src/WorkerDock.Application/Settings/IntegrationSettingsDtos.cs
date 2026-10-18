using System.Text.Json;

namespace WorkerDock.Application.Settings;

public sealed record VaultSettingsDto(string ProviderId, JsonElement PublicOptions, byte[] Version);

public sealed record EgressPolicyDto(string Name, IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports,
    IReadOnlyList<string> PrivateCidrs, bool AllowHttp, byte[] Version);

public sealed record IntegrationConfigurationDto(int Version, long VaultRevision, string ProviderId,
    JsonElement PublicOptions, IReadOnlyList<EgressPolicyDto> Policies);

public static class IntegrationConfigurationJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement Serialize(IntegrationConfigurationDto snapshot) => JsonSerializer.SerializeToElement(snapshot, Options);

    public static IntegrationConfigurationDto Deserialize(JsonElement json) =>
        json.Deserialize<IntegrationConfigurationDto>(Options) ?? throw new JsonException("Integration snapshot is missing.");
}
