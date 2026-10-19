using System.Text.RegularExpressions;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.ControlCenter.Registry;

public sealed record TenantRegistration(Guid Id, string DisplayName, Guid ExpectedInstanceId, Uri DashboardUrl,
    SecretReference HealthCredential, byte[] Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record TenantRegistrationInput(string DisplayName, Guid ExpectedInstanceId, string DashboardUrl, string HealthCredentialReference);
public sealed record TenantDirectoryEntry(Guid Id, string DisplayName, Guid ExpectedInstanceId, Uri DashboardUrl, string Health,
    DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastSuccessUtc, byte[] Version);

public interface ICentralRegistry
{
    ValueTask<Page<TenantDirectoryEntry>> ListAsync(string? search, PageRequest page, CancellationToken ct);
    ValueTask<TenantRegistration?> GetAsync(Guid id, CancellationToken ct);
    ValueTask<TenantDirectoryEntry> SaveAsync(Guid? id, TenantRegistrationInput input, byte[]? expectedVersion, ActorContext actor, CancellationToken ct);
    ValueTask RemoveAsync(Guid id, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
    ValueTask<TenantDirectoryEntry> RecordHealthAsync(Guid id, string health, string? safeCode, DateTimeOffset attemptedUtc, DateTimeOffset? succeededUtc, CancellationToken ct);
}

internal static class TenantDirectoryCursor
{
    public static string Encode(TenantDirectoryEntry entry) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(entry.DisplayName + "\n" + entry.Id.ToString("D")));
    public static (string Name, Guid Id)? Decode(string? value)
    {
        if (value is null) return null;
        try
        {
            if (value.Length > 1024) throw new FormatException();
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('\n');
            if (parts.Length != 2 || parts[0].Length > 120 || !Guid.TryParseExact(parts[1], "D", out var id)) throw new FormatException();
            return (parts[0], id);
        }
        catch (FormatException) { throw new WorkerDockException("ValidationFailed", "cursor"); }
    }
    public static void Validate(PageRequest page)
    {
        if (page.Limit is < 1 or > 100) throw new WorkerDockException("ValidationFailed", "limit");
        _ = Decode(page.Cursor);
    }
}

public static partial class TenantRegistrationValidation
{
    public static TenantRegistrationInput Validate(TenantRegistrationInput input)
    {
        if (input.DisplayName is null || string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 120)
            throw new WorkerDockException("ValidationFailed", "displayName");
        if (input.ExpectedInstanceId == Guid.Empty) throw new WorkerDockException("ValidationFailed", "expectedInstanceId");
        if (!Uri.TryCreate(input.DashboardUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
            url.HostNameType == UriHostNameType.Unknown || url.Port != 443 || input.DashboardUrl.Length > 2048)
            throw new WorkerDockException("ValidationFailed", "dashboardUrl");
        if (input.HealthCredentialReference is null || !CredentialReferenceRegex().IsMatch(input.HealthCredentialReference))
            throw new WorkerDockException("ValidationFailed", "healthCredentialReference");
        var basePath = url.GetLeftPart(UriPartial.Path);
        return input with { DisplayName = input.DisplayName.Trim(), DashboardUrl = basePath.EndsWith('/') ? basePath : basePath + "/" };
    }

    [GeneratedRegex("^[A-Z0-9_]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialReferenceRegex();
}
