using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Settings;

namespace WorkerDock.Infrastructure.Settings;

/// <summary>Development-only settings adapter; it does not imply that SQL persistence is configured.</summary>
public sealed class InMemoryIntegrationSettings : IIntegrationSettings
{
    private readonly object gate = new();
    private VaultSettingsDto vault = new("workerdock.environment", JsonSerializer.SerializeToElement(new { }), BitConverter.GetBytes(1L));
    private long vaultRevision = 1;
    private readonly Dictionary<string, EgressPolicyDto> egress = new(StringComparer.Ordinal)
    {
        ["tenant-jobs"] = new("tenant-jobs", [], [443], [], false, BitConverter.GetBytes(1L)),
        ["tenant-alerts"] = new("tenant-alerts", [], [443], [], false, BitConverter.GetBytes(1L))
    };
    private readonly List<IntegrationSettingsAudit> audits = [];

    public IReadOnlyList<IntegrationSettingsAudit> AuditEvents { get { lock (gate) return audits.ToArray(); } }

    public ValueTask<VaultSettingsDto> GetVaultAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (gate) return ValueTask.FromResult(Clone(vault)); }

    public ValueTask<CommandReceipt> SaveVaultAsync(VaultSettingsDto settings, ActorContext actor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); IntegrationSettingsValidation.ValidateActor(actor);
        var options = IntegrationSettingsValidation.ValidateVault(settings.ProviderId, settings.PublicOptions);
        lock (gate)
        {
            if (settings.Version is null || !vault.Version.AsSpan().SequenceEqual(settings.Version)) throw new WorkerDockException("StaleVersion");
            var oldRevision = vaultRevision; vaultRevision++;
            vault = new(settings.ProviderId, options, BitConverter.GetBytes(vaultRevision));
            var receipt = new CommandReceipt(Guid.NewGuid(), Guid.Empty, "Applied");
            audits.Add(new("VaultSettingsSaved", actor, oldRevision, vaultRevision));
            return ValueTask.FromResult(receipt);
        }
    }

    public ValueTask<EgressPolicyDto> GetEgressAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (gate) return ValueTask.FromResult(egress.TryGetValue(name, out var item) ? Clone(item) : throw new WorkerDockException("EgressPolicyNotFound"));
    }

    public ValueTask<CommandReceipt> SaveEgressAsync(EgressPolicyDto policy, ActorContext actor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); IntegrationSettingsValidation.ValidateActor(actor);
        var validated = IntegrationSettingsValidation.ValidateEgress(policy);
        lock (gate)
        {
            if (!egress.TryGetValue(validated.Name, out var old)) throw new WorkerDockException("EgressPolicyNotFound");
            if (validated.Version is null || !old.Version.AsSpan().SequenceEqual(validated.Version)) throw new WorkerDockException("StaleVersion");
            var revision = BitConverter.ToInt64(old.Version) + 1;
            egress[validated.Name] = validated with { Version = BitConverter.GetBytes(revision) };
            var receipt = new CommandReceipt(Guid.NewGuid(), Guid.Empty, "Applied");
            audits.Add(new("EgressSettingsSaved", actor, revision - 1, revision));
            return ValueTask.FromResult(receipt);
        }
    }

    public ValueTask<IntegrationConfigurationDto> GetSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (gate)
            return ValueTask.FromResult(IntegrationSettingsValidation.ValidateSnapshot(new IntegrationConfigurationDto(1, vaultRevision, vault.ProviderId, vault.PublicOptions.Clone(), egress.Values.OrderBy(x => x.Name, StringComparer.Ordinal).Select(Clone).ToArray())));
    }

    private static VaultSettingsDto Clone(VaultSettingsDto value) => value with { PublicOptions = value.PublicOptions.Clone(), Version = (byte[])value.Version.Clone() };
    private static EgressPolicyDto Clone(EgressPolicyDto value) => value with { Hosts = value.Hosts.ToArray(), Ports = value.Ports.ToArray(), PrivateCidrs = value.PrivateCidrs.ToArray(), Version = (byte[])value.Version.Clone() };
}

public sealed record IntegrationSettingsAudit(string Kind, ActorContext Actor, long BeforeRevision, long AfterRevision);
