using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Infrastructure.Persistence;

public sealed record TenantSqlSettings(string Host, int Port, string Database, string User, SecretReference PasswordReference);

public static class TenantSqlConfiguration
{
    public static TenantSqlSettings Read(IConfiguration config, string section)
    {
        var sql = config.GetSection(section);
        if (!int.TryParse(sql["Port"], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new InvalidOperationException("SqlConfigurationInvalid");

        var settings = new TenantSqlSettings(sql["Host"]!, port, sql["Database"]!, sql["User"]!, new(sql["PasswordReference"]!));
        Validate(settings);
        return settings;
    }

    public static void RejectLegacy(IConfiguration config, string key)
    {
        if (config[key] is not null)
            throw new InvalidOperationException("LegacyConnectionStringUnsupported");
    }

    internal static void Validate(TenantSqlSettings settings)
    {
        if (settings is null || !ValidHost(settings.Host) || settings.Port is < 1 or > 65535 ||
            !ValidText(settings.Database) || !ValidText(settings.User) ||
            settings.PasswordReference?.Name is not { Length: > 0 and <= 128 } reference ||
            !reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new InvalidOperationException("SqlConfigurationInvalid");
    }

    private static bool ValidText(string value) =>
        value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);

    private static bool ValidHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 ||
            host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '\\' or ';' or '=' or '/' or '[' or ']'))
            return false;
        if (IPAddress.TryParse(host, out _)) return true;
        var dnsName = host.EndsWith('.') ? host[..^1] : host;
        return dnsName.Split('.').All(label =>
            label.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
