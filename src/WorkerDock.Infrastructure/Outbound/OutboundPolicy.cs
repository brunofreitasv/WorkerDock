using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WorkerDock.Application.Outbound;

namespace WorkerDock.Infrastructure.Outbound;

public interface IOutboundAddressResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed record OutboundPolicyRule(
    IReadOnlySet<string> Hosts,
    IReadOnlySet<int> Ports,
    bool AllowHttp = false,
    IReadOnlyList<string>? PrivateCidrs = null);

/// <summary>Resolves once for each connection and rejects the entire answer set if any address is unsafe.</summary>
public sealed class OutboundPolicy : IOutboundPolicy
{
    private static readonly string[] HardDeniedCidrs =
    [
        "0.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "168.63.129.16/32", "169.254.0.0/16", "192.0.0.0/24", "192.0.2.0/24",
        "192.88.99.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "::/128", "::1/128", "::ffff:0:0/96", "64:ff9b::/96", "64:ff9b:1::/48", "100::/64", "2001::/23", "2001:db8::/32", "2002::/16", "fe80::/10", "ff00::/8"
    ];
    private static readonly string[] PrivateCidrs = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];
    private readonly IReadOnlyDictionary<string, OutboundPolicyRule> _rules;
    private readonly IOutboundAddressResolver _resolver;

    public OutboundPolicy(IOutboundAddressResolver? resolver = null, IReadOnlyDictionary<string, OutboundPolicyRule>? rules = null)
    {
        _resolver = resolver ?? new SystemOutboundAddressResolver();
        _rules = rules ?? new Dictionary<string, OutboundPolicyRule>(StringComparer.Ordinal)
        {
            ["tenant-jobs"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<int> { 443 }),
            ["tenant-alerts"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<int> { 443 }),
            ["central-health"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<int> { 443 })
        };
    }

    public async ValueTask<ApprovedDestination> ResolveAsync(OutboundTarget target, CancellationToken ct)
    {
        try
        {
            var uri = target.Uri;
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                uri.HostNameType == UriHostNameType.Unknown || uri.Host.Contains('%') || target.PolicyName is not { Length: > 0 and <= 64 })
                throw new OutboundPolicyException("OutboundTargetInvalid");
            if (!_rules.TryGetValue(target.PolicyName, out var rule)) throw new OutboundPolicyException("OutboundPolicyUnavailable");
            if ((uri.Scheme == "http" && !rule.AllowHttp) || !rule.Ports.Contains(uri.Port)) throw new OutboundPolicyException("OutboundPortDenied");

            var host = NormalizeHost(uri.IdnHost);
            if (!HostAllowed(host, rule.Hosts)) throw new OutboundPolicyException("OutboundHostDenied");
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
            else addresses = await _resolver.ResolveAsync(host, ct);
            if (addresses.Length is 0 or > 32) throw new OutboundPolicyException("OutboundAddressUnavailable");
            var normalized = addresses.Select(NormalizeAddress).Distinct().ToArray();
            var privateAllow = (rule.PrivateCidrs ?? []).Select(ParseCidr).ToArray();
            foreach (var address in normalized)
            {
                if (IsHardDenied(address) || IsPrivate(address) && !privateAllow.Any(cidr => cidr.Contains(address)))
                    throw new OutboundPolicyException("OutboundAddressDenied");
                if (!IsPrivate(address) && !IsPublic(address)) throw new OutboundPolicyException("OutboundAddressDenied");
            }
            return new(host, uri.Port, normalized);
        }
        catch (OutboundPolicyException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new OutboundPolicyException("OutboundResolutionFailed"); }
    }

    public static string NormalizeHost(string value)
    {
        var host = value.Trim().TrimEnd('.');
        if (host.Length == 0 || host.Length > 253) throw new OutboundPolicyException("OutboundTargetInvalid");
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return NormalizeAddress(ip).ToString().ToLowerInvariant();
        try { host = new IdnMapping().GetAscii(host); }
        catch { throw new OutboundPolicyException("OutboundTargetInvalid"); }
        return host.ToLowerInvariant();
    }

    private static bool HostAllowed(string host, IReadOnlySet<string> allowed)
    {
        if (allowed.Count == 0) return true; // Public addresses remain mandatory; this is a deployment scope, not an address bypass.
        foreach (var candidate in allowed)
        {
            var normalized = NormalizeHost(candidate.TrimStart('*', '.'));
            if (host == normalized || candidate.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith("." + normalized, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private static bool IsPrivate(IPAddress ip) => InAny(ip, PrivateCidrs);
    private static bool IsHardDenied(IPAddress ip) => InAny(ip, HardDeniedCidrs);
    private static bool IsPublic(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork) return !InAny(ip, ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]);
        return InAny(ip, ["2000::/3"]);
    }
    private static bool InAny(IPAddress ip, IEnumerable<string> cidrs) => cidrs.Any(c => ParseCidr(c).Contains(ip));
    private static Cidr ParseCidr(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bits) || bits < 0 || bits > address.GetAddressBytes().Length * 8)
            throw new OutboundPolicyException("OutboundPolicyInvalid");
        return new(NormalizeAddress(address), bits);
    }
    private sealed record Cidr(IPAddress Network, int Bits)
    {
        public bool Contains(IPAddress value)
        {
            value = NormalizeAddress(value);
            var net = NormalizeAddress(Network).GetAddressBytes(); var address = value.GetAddressBytes();
            if (net.Length != address.Length) return false;
            var whole = Bits / 8; var remaining = Bits % 8;
            for (var i = 0; i < whole; i++) if (net[i] != address[i]) return false;
            return remaining == 0 || (net[whole] & (0xff << (8 - remaining))) == (address[whole] & (0xff << (8 - remaining)));
        }
    }

    private sealed class SystemOutboundAddressResolver : IOutboundAddressResolver
    {
        public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => await Dns.GetHostAddressesAsync(host, ct);
    }
}
