using WorkerDock.Application.Common;
using WorkerDock.Application.Connections;
using WorkerDock.Contracts.Connections;
using WorkerDock.Application.Jobs;
namespace WorkerDock.Infrastructure.Connections;
/// <summary>Development-only adapter. This store provides no evidence of SQL target effects.</summary>
public sealed class InMemoryConnectionProfiles : IConnectionProfiles
{
    private readonly object gate=new();
    private readonly List<ProfileAudit> audits=[];
    public IReadOnlyList<ProfileAudit> AuditEvents { get { lock(gate)return audits.ToArray(); } }
    private readonly Dictionary<Guid,ProfileEditorDto> profiles=[];
    private readonly Dictionary<Guid,ConnectionProfileRevision> revisions=[];
    public ValueTask<CommandReceipt> SaveAsync(ConnectionProfileRevision profile,byte[]? expectedVersion,ActorContext actor,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ConnectionProfileValidation.Validate(profile,actor);
        lock(gate)
        {
            profiles.TryGetValue(profile.ProfileId,out var old);
            if(old is not null && old.Retired) throw new WorkerDockException("ProfileRetired");
            if(old is null && expectedVersion is not null || old is not null && (expectedVersion is null || !old.Version.AsSpan().SequenceEqual(expectedVersion))) throw new WorkerDockException("StaleVersion");
            if(old is null && profiles.Count>=ConnectionProfileValidation.MaximumProfiles) throw new WorkerDockException("ProfileLimitReached");
            if(profiles.Values.Any(x=>x.Current.ProfileId!=profile.ProfileId && string.Equals(x.Current.Name,profile.Name,StringComparison.OrdinalIgnoreCase))) throw new WorkerDockException("ProfileNameConflict");
            var number=(old?.Current.Revision ?? 0)+1;
            var revision=ConnectionProfileValidation.Freeze(profile with { ProfileRevisionId=Guid.NewGuid(),Revision=number });
            audits.Add(new(profile.ProfileId,"ProfileSaved",old?.Current.Revision,number,actor));
            revisions.Add(revision.ProfileRevisionId,revision);profiles[profile.ProfileId]=new(revision,BitConverter.GetBytes(number),false);
            return ValueTask.FromResult(new CommandReceipt(Guid.NewGuid(),profile.ProfileId,"Applied"));
        }
    }
    public ValueTask<IReadOnlyList<ProfileEditorDto>> ListAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested();lock(gate)return ValueTask.FromResult<IReadOnlyList<ProfileEditorDto>>(profiles.Values.Where(x=>!x.Retired).OrderBy(x=>x.Current.Name,StringComparer.OrdinalIgnoreCase).Select(Copy).ToArray()); }
    public ValueTask<ProfileEditorDto> GetCurrentAsync(Guid profileId,CancellationToken ct)
    { ct.ThrowIfCancellationRequested();lock(gate){if(!profiles.TryGetValue(profileId,out var profile))throw new WorkerDockException("ProfileNotFound");if(profile.Retired)throw new WorkerDockException("ProfileRetired");return ValueTask.FromResult(Copy(profile));} }
    public ValueTask<ConnectionProfileRevision> GetRevisionAsync(Guid id,CancellationToken ct)
    { ct.ThrowIfCancellationRequested();lock(gate)return ValueTask.FromResult(revisions.TryGetValue(id,out var r)?ConnectionProfileValidation.Freeze(r):throw new WorkerDockException("ProfileNotFound")); }
    public ValueTask<CommandReceipt> RetireAsync(Guid id,byte[] expectedVersion,ActorContext actor,CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);ct.ThrowIfCancellationRequested();lock(gate)
        {
            if(!profiles.TryGetValue(id,out var profile))throw new WorkerDockException("ProfileNotFound");
            if(!profile.Version.AsSpan().SequenceEqual(expectedVersion))throw new WorkerDockException("StaleVersion");
            audits.Add(new(id,"ProfileRetired",profile.Current.Revision,profile.Current.Revision,actor));
            profiles[id]=profile with { Retired=true,Version=BitConverter.GetBytes(profile.Current.Revision+1) };
            return ValueTask.FromResult(new CommandReceipt(Guid.NewGuid(),id,"Applied"));
        }
    }
    private static ProfileEditorDto Copy(ProfileEditorDto value)=>value with { Current=ConnectionProfileValidation.Freeze(value.Current),Version=value.Version.ToArray() };
}

public sealed record ProfileAudit(Guid ProfileId,string Kind,long? BeforeRevision,long AfterRevision,ActorContext Actor);
