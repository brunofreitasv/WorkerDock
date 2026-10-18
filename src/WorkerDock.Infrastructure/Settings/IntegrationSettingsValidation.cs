using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Settings;
using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.Infrastructure.Settings;

internal static class IntegrationSettingsValidation
{
    internal static readonly string[] Names = ["tenant-jobs", "tenant-alerts"];
    private static readonly string[] HardDenied = ["0.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "168.63.129.16/32", "169.254.0.0/16", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4", "::/128", "::1/128", "::ffff:0:0/96", "64:ff9b::/96", "64:ff9b:1::/48", "100::/64", "2001::/23", "2001:db8::/32", "2002::/16", "fe80::/10", "ff00::/8"];

    internal static void ValidateActor(ActorContext actor)
    {
        if (actor is null || string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 300 ||
            string.IsNullOrWhiteSpace(actor.AuthenticationMode) || actor.AuthenticationMode.Length > 40 || actor.CorrelationId == Guid.Empty)
            throw new WorkerDockException("ValidationFailed", "actor");
    }

    internal static JsonElement ValidateVault(string providerId, JsonElement options)
    {
        if (providerId != SecretVaultRegistration.EnvironmentMetadata.Id)
            throw new WorkerDockException("ProviderNotInstalled", "providerId");
        if (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any() || JsonSerializer.SerializeToUtf8Bytes(options).Length > 1024)
            throw new WorkerDockException("ValidationFailed", "publicOptions");
        var issues = TemplateSchemaValidator.ValidateProviderOptions(SecretVaultRegistration.EnvironmentMetadata, options);
        if (issues.Count != 0) throw new WorkerDockException("ValidationFailed", "publicOptions");
        return options.Clone();
    }

    internal static IntegrationConfigurationDto ValidateSnapshot(IntegrationConfigurationDto snapshot)
    {
        if (snapshot.Version != 1 || snapshot.VaultRevision < 1 || snapshot.Policies is null || snapshot.Policies.Count != Names.Length)
            throw new WorkerDockException("SettingsUnavailable");
        var providerOptions = ValidateVault(snapshot.ProviderId, snapshot.PublicOptions);
        var policies = snapshot.Policies.Select(ValidateEgress).ToArray();
        if (policies.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != Names.Length ||
            Names.Any(name => policies.All(policy => policy.Name != name)))
            throw new WorkerDockException("SettingsUnavailable");
        var result = snapshot with { PublicOptions = providerOptions, Policies = policies };
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > 3800) throw new WorkerDockException("SettingsUnavailable");
        return result;
    }

    internal static EgressPolicyDto ValidateEgress(EgressPolicyDto policy)
    {
        if (policy?.PrivateCidrs?.Any(IsHardDeniedCidr) == true) throw new WorkerDockException("DeploymentPolicyViolation", "privateCidrs");
        if (policy is null || !Names.Contains(policy.Name, StringComparer.Ordinal) || policy.Hosts is null || policy.Ports is null || policy.PrivateCidrs is null ||
            policy.Hosts.Count > 20 || policy.Ports.Count is < 1 or > 32 || policy.PrivateCidrs.Count > 16 ||
            policy.Hosts.Any(host => !ValidHost(host)) || policy.Hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.Hosts.Count ||
            policy.Ports.Any(port => port is < 1 or > 65535) || policy.Ports.Distinct().Count() != policy.Ports.Count ||
            policy.PrivateCidrs.Any(cidr => !ValidPrivateCidr(cidr)) || policy.PrivateCidrs.Distinct(StringComparer.Ordinal).Count() != policy.PrivateCidrs.Count)
            throw new WorkerDockException("ValidationFailed", "egress");
        var normalized = policy with { Hosts = policy.Hosts.ToArray(), Ports = policy.Ports.Order().ToArray(), PrivateCidrs = policy.PrivateCidrs.ToArray(), Version = (byte[])policy.Version.Clone() };
        if (JsonSerializer.SerializeToUtf8Bytes(normalized).Length > 1000) throw new WorkerDockException("ValidationFailed", "egress");
        return normalized;
    }

    private static bool ValidHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value != value.Trim() || value.Contains('/') || value.Contains('@') || value.Contains('%')) return false;
        if (IPAddress.TryParse(value, out _)) return !value.StartsWith("*.", StringComparison.Ordinal);
        if (value.Contains(':')) return false;
        var host = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        if (host.Contains('*') || host.EndsWith('.')) return false;
        try { return Uri.CheckHostName(new System.Globalization.IdnMapping().GetAscii(host)) == UriHostNameType.Dns; }
        catch { return false; }
    }

    private static bool ValidPrivateCidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix)) return false;
        var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < (max == 32 ? 8 : 7) || prefix > max) return false;
        var privateNetwork = ip.AddressFamily == AddressFamily.InterNetwork
            ? In(ip, "10.0.0.0/8") || In(ip, "172.16.0.0/12") || In(ip, "192.168.0.0/16")
            : In(ip, "fc00::/7");
        if (!privateNetwork || HardDenied.Any(range => Overlaps(ip, prefix, range))) return false;
        return true;
    }

    private static bool IsHardDeniedCidr(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > address.GetAddressBytes().Length * 8) return false;
        return HardDenied.Any(range => Overlaps(address, prefix, range));
    }

    private static bool In(IPAddress ip, string cidr) => Contains(cidr, ip);
    private static bool Overlaps(IPAddress address, int prefix, string deny)
    {
        var parts = deny.Split('/'); var network = IPAddress.Parse(parts[0]); var bits = int.Parse(parts[1]);
        if (network.AddressFamily != address.AddressFamily) return false;
        return Contains(address.ToString() + "/" + prefix, network) || Contains(deny, address);
    }
    private static bool Contains(string cidr, IPAddress value)
    {
        var parts = cidr.Split('/'); var network = IPAddress.Parse(parts[0]); var bits = int.Parse(parts[1]);
        var a = network.GetAddressBytes(); var b = value.GetAddressBytes(); if (a.Length != b.Length) return false;
        for (var bit = 0; bit < bits; bit++) if ((a[bit / 8] & (1 << (7 - bit % 8))) != (b[bit / 8] & (1 << (7 - bit % 8)))) return false;
        return true;
    }
}
