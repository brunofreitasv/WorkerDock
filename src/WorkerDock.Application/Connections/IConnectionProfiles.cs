using WorkerDock.Application.Common;
using WorkerDock.Contracts.Connections;
namespace WorkerDock.Application.Connections;
public sealed record ProfileEditorDto(ConnectionProfileRevision Current, byte[] Version, bool Retired);
public interface IConnectionProfiles
{
    ValueTask<CommandReceipt> SaveAsync(ConnectionProfileRevision profile, byte[]? expectedVersion, ActorContext actor, CancellationToken ct);
    ValueTask<IReadOnlyList<ProfileEditorDto>> ListAsync(CancellationToken ct);
    ValueTask<ProfileEditorDto> GetCurrentAsync(Guid profileId, CancellationToken ct);
    ValueTask<ConnectionProfileRevision> GetRevisionAsync(Guid profileRevisionId, CancellationToken ct);
    ValueTask<CommandReceipt> RetireAsync(Guid profileId, byte[] expectedVersion, ActorContext actor, CancellationToken ct);
}
public interface IProfileDiagnostics
{
    ValueTask<CommandReceipt> StartAsync(Guid profileId, string catalog, ActorContext actor, CancellationToken ct);
}
