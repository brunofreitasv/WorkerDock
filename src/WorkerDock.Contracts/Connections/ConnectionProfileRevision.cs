using System.Text.Json.Serialization;
using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Contracts.Connections;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConnectionProfileRevision(Guid ProfileRevisionId, Guid ProfileId, long Revision, string Server,
    IReadOnlyList<string> AllowedCatalogs, string Authentication, string? UserName, SecretReference? PasswordReference,
    bool Encrypt, bool TrustServerCertificate)
{
    public string Name { get; init; } = "SQL Server profile";
}
