using System.Text.RegularExpressions;
namespace WorkerDock.Infrastructure.Authentication;

public sealed record AccessOptions(string? Authority, string? ClientId, string? ClientSecretReference, string? RequiredManagementRole, string RoleClaimType = "roles")
{
    public bool AuthenticationConfigured => !string.IsNullOrEmpty(Authority);
    public string Mode => !AuthenticationConfigured ? "Anonymous" : RequiredManagementRole is null ? "Authenticated" : "RoleProtected";
    public static IReadOnlyList<string> ValidateAccessOptions(AccessOptions options)
    {
        var errors = new List<string>();
        var supplied = new[] { options.Authority, options.ClientId, options.ClientSecretReference }.Count(x => x is not null);
        if (supplied is > 0 and < 3) errors.Add("IncompleteAuthentication");
        if (options.RequiredManagementRole is not null && supplied == 0) errors.Add("RoleRequiresAuthentication");
        if (options.RequiredManagementRole is not null && string.IsNullOrWhiteSpace(options.RequiredManagementRole)) errors.Add("InvalidManagementRole");
        if (string.IsNullOrWhiteSpace(options.RoleClaimType)) errors.Add("InvalidRoleClaimType");
        if (supplied == 3)
        {
            if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.Segments.Any(x => new[] { "common", "organizations", "consumers" }.Contains(x.Trim('/'), StringComparer.OrdinalIgnoreCase))) errors.Add("InvalidAuthority");
            if (string.IsNullOrWhiteSpace(options.ClientId)) errors.Add("InvalidClientId");
            if (options.ClientSecretReference is null || !Regex.IsMatch(options.ClientSecretReference, "^[A-Z0-9_]{1,128}$", RegexOptions.CultureInvariant)) errors.Add("InvalidCredentialReference");
        }
        return errors;
    }
}
