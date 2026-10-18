using WorkerDock.Application.Common;

namespace WorkerDock.Application.Settings;

public interface IIntegrationSettings
{
    ValueTask<VaultSettingsDto> GetVaultAsync(CancellationToken ct);
    ValueTask<CommandReceipt> SaveVaultAsync(VaultSettingsDto settings, ActorContext actor, CancellationToken ct);
    ValueTask<EgressPolicyDto> GetEgressAsync(string name, CancellationToken ct);
    ValueTask<CommandReceipt> SaveEgressAsync(EgressPolicyDto policy, ActorContext actor, CancellationToken ct);
    ValueTask<IntegrationConfigurationDto> GetSnapshotAsync(CancellationToken ct);
}
