using WorkerDock.Application.Common;
using WorkerDock.Application.Connections;
using WorkerDock.Contracts.Connections;
namespace WorkerDock.Infrastructure.Connections;
public sealed class UnavailableProfileDiagnostics : IProfileDiagnostics
{
    public ValueTask<CommandReceipt> StartAsync(Guid id,string catalog,ActorContext actor,CancellationToken ct)=>ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
}
public sealed class UnavailableConnectionProfiles : IConnectionProfiles
{
    public ValueTask<CommandReceipt> SaveAsync(ConnectionProfileRevision profile,byte[]? version,ActorContext actor,CancellationToken ct)=>ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<IReadOnlyList<ProfileEditorDto>> ListAsync(CancellationToken ct)=>ValueTask.FromException<IReadOnlyList<ProfileEditorDto>>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<ProfileEditorDto> GetCurrentAsync(Guid id,CancellationToken ct)=>ValueTask.FromException<ProfileEditorDto>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<ConnectionProfileRevision> GetRevisionAsync(Guid id,CancellationToken ct)=>ValueTask.FromException<ConnectionProfileRevision>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<CommandReceipt> RetireAsync(Guid id,byte[] version,ActorContext actor,CancellationToken ct)=>ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
}
