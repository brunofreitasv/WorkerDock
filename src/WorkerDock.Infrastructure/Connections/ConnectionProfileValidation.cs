using WorkerDock.Application.Jobs;
using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Connections;
using WorkerDock.Infrastructure.Jobs;
namespace WorkerDock.Infrastructure.Connections;
internal static class ConnectionProfileValidation
{
    internal const int MaximumProfiles=200;
    internal static ConnectionProfileRevision Freeze(ConnectionProfileRevision profile)=>profile with { AllowedCatalogs=profile.AllowedCatalogs.ToArray() };
    internal static void Validate(ConnectionProfileRevision profile,ActorContext actor)
    {
        JobCommandValidation.ValidateActor(actor);
        if(profile.ProfileId==Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length>200 || profile.Name!=profile.Name.Trim() ||
            string.IsNullOrWhiteSpace(profile.Server) || profile.Server.Length>256 || profile.Server.Any(char.IsControl) ||
            profile.AllowedCatalogs is not { Count: >0 and <=64 } || profile.AllowedCatalogs.Any(x=>string.IsNullOrWhiteSpace(x) || x.Length>128 || x.Any(char.IsControl)) ||
            profile.AllowedCatalogs.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=profile.AllowedCatalogs.Count ||
            profile.Authentication is not ("Integrated" or "VaultPassword") ||
            profile.Authentication=="Integrated" && (profile.UserName is not null || profile.PasswordReference is not null) ||
            profile.Authentication=="VaultPassword" && (string.IsNullOrWhiteSpace(profile.UserName) || profile.UserName.Length>128 || profile.PasswordReference?.Name is not { Length: >0 and <=200 }))
            throw new WorkerDockException("ValidationFailed","profile");
        if(JsonSerializer.SerializeToUtf8Bytes(profile).Length>4096) throw new WorkerDockException("ValidationFailed","profile");
    }
}
