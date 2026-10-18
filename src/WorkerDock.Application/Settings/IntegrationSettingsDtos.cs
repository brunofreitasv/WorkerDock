using System.Text.Json;

namespace WorkerDock.Application.Settings;

public sealed record VaultSettingsDto(string ProviderId, JsonElement PublicOptions, byte[] Version);

public sealed record EgressPolicyDto(string Name, IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports,
    IReadOnlyList<string> PrivateCidrs, bool AllowHttp, byte[] Version);

public sealed record IntegrationConfigurationDto(int Version, long VaultRevision, string ProviderId,
    JsonElement PublicOptions, IReadOnlyList<EgressPolicyDto> Policies);
