using WorkerDock.Application.Jobs;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Connections;
using WorkerDock.Contracts.Connections;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Jobs;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;
namespace WorkerDock.Infrastructure.Connections;
public sealed class SqlConnectionProfiles(TenantDatabaseOptions options) : IConnectionProfiles
{
    public async ValueTask<CommandReceipt> SaveAsync(ConnectionProfileRevision profile,byte[]? expectedVersion,ActorContext actor,CancellationToken ct)
    {
        ConnectionProfileValidation.Validate(profile,actor);
        return await RunAsync(async(c,t,token)=>
        {
            var old=await ReadCurrentAsync(c,t,profile.ProfileId,token);
            if(old?.Retired==true)throw new WorkerDockException("ProfileRetired");
            if(old is null && expectedVersion is not null || old is not null && (expectedVersion is null || !old.Version.AsSpan().SequenceEqual(expectedVersion)))throw new WorkerDockException("StaleVersion");
            await using var count=Command(c,t,"SELECT COUNT(*) FROM [workerdock].ConnectionProfiles");
            if(old is null && (int)(await count.ExecuteScalarAsync(token))!>=ConnectionProfileValidation.MaximumProfiles)throw new WorkerDockException("ProfileLimitReached");
            await using var name=Command(c,t,"SELECT COUNT(*) FROM [workerdock].ConnectionProfiles WHERE Name=@name AND ProfileId<>@id");
            Add(name,"@name",SqlDbType.NVarChar,profile.Name,200);Add(name,"@id",SqlDbType.UniqueIdentifier,profile.ProfileId);
            if((int)(await name.ExecuteScalarAsync(token))!>0)throw new WorkerDockException("ProfileNameConflict");
            var revision=ConnectionProfileValidation.Freeze(profile with { ProfileRevisionId=Guid.NewGuid(),Revision=(old?.Current.Revision ?? 0)+1 });
            if(old is null)
            {
                await using var insert=Command(c,t,"INSERT [workerdock].ConnectionProfiles(ProfileId,Name,CurrentRevisionId,Retired) VALUES(@id,@name,NULL,0)");
                Add(insert,"@id",SqlDbType.UniqueIdentifier,profile.ProfileId);Add(insert,"@name",SqlDbType.NVarChar,profile.Name,200);await insert.ExecuteNonQueryAsync(token);
            }
            await using var save=Command(c,t,"INSERT [workerdock].ConnectionProfileRevisions(ProfileRevisionId,ProfileId,Revision,ConfigurationJson,CreatedAtUtc) VALUES(@revision,@id,@number,@json,SYSUTCDATETIME()); UPDATE [workerdock].ConnectionProfiles SET CurrentRevisionId=@revision,Name=@name WHERE ProfileId=@id");
            Add(save,"@revision",SqlDbType.UniqueIdentifier,revision.ProfileRevisionId);Add(save,"@id",SqlDbType.UniqueIdentifier,profile.ProfileId);Add(save,"@number",SqlDbType.BigInt,revision.Revision);Add(save,"@json",SqlDbType.NVarChar,JsonSerializer.Serialize(revision),-1);Add(save,"@name",SqlDbType.NVarChar,profile.Name,200);await save.ExecuteNonQueryAsync(token);
            return await ReceiptAsync(c,t,profile.ProfileId,"ProfileSaved",actor,token,beforeRevision:old?.Current.Revision,afterRevision:revision.Revision);
        },ct);
    }
    public ValueTask<IReadOnlyList<ProfileEditorDto>> ListAsync(CancellationToken ct)=>RunAsync<IReadOnlyList<ProfileEditorDto>>(async(c,t,token)=>
    {
        await using var command=Command(c,t,"SELECT TOP (200) r.ConfigurationJson,p.Version,p.Retired FROM [workerdock].ConnectionProfiles p JOIN [workerdock].ConnectionProfileRevisions r ON r.ProfileRevisionId=p.CurrentRevisionId WHERE p.Retired=0 ORDER BY p.Name,p.ProfileId");
        await using var reader=await command.ExecuteReaderAsync(token);var list=new List<ProfileEditorDto>();while(await reader.ReadAsync(token))list.Add(Read(reader));return list;
    },ct);
    public ValueTask<ProfileEditorDto> GetCurrentAsync(Guid id,CancellationToken ct)=>RunAsync(async(c,t,token)=>
    {
        var profile=await ReadCurrentAsync(c,t,id,token) ?? throw new WorkerDockException("ProfileNotFound");
        return profile.Retired ? throw new WorkerDockException("ProfileRetired") : profile;
    },ct);
    public ValueTask<ConnectionProfileRevision> GetRevisionAsync(Guid id,CancellationToken ct)=>RunAsync(async(c,t,token)=>
    {
        await using var command=Command(c,t,"SELECT ConfigurationJson FROM [workerdock].ConnectionProfileRevisions WHERE ProfileRevisionId=@id");Add(command,"@id",SqlDbType.UniqueIdentifier,id);
        return await command.ExecuteScalarAsync(token) is string json ? JsonSerializer.Deserialize<ConnectionProfileRevision>(json)! : throw new WorkerDockException("ProfileNotFound");
    },ct);
    public ValueTask<CommandReceipt> RetireAsync(Guid id,byte[] expectedVersion,ActorContext actor,CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        return RunAsync(async(c,t,token)=>
        {
            var profile=await ReadCurrentAsync(c,t,id,token) ?? throw new WorkerDockException("ProfileNotFound");
            if(!profile.Version.AsSpan().SequenceEqual(expectedVersion))throw new WorkerDockException("StaleVersion");
            await using var command=Command(c,t,"UPDATE [workerdock].ConnectionProfiles SET Retired=1 WHERE ProfileId=@id");Add(command,"@id",SqlDbType.UniqueIdentifier,id);await command.ExecuteNonQueryAsync(token);
            return await ReceiptAsync(c,t,id,"ProfileRetired",actor,token);
        },ct);
    }
    private async ValueTask<T> RunAsync<T>(Func<DbConnection,DbTransaction,CancellationToken,ValueTask<T>> action,CancellationToken ct)
    {
        try { return await new BusinessTransaction(options).RunAsync(async(c,t,token)=>
        {
            await using var identity=Command(c,t,"SELECT InstanceId FROM [workerdock].TenantSettings");if((Guid?)await identity.ExecuteScalarAsync(token)!=options.InstanceId)throw new WorkerDockException("TenantInstanceMismatch");
            return await action(c,t,token);
        },ct); }
        catch(SqlException){throw new WorkerDockException("PersistenceUnavailable");}
    }
    private static async ValueTask<ProfileEditorDto?> ReadCurrentAsync(DbConnection c,DbTransaction t,Guid id,CancellationToken ct)
    {
        await using var command=Command(c,t,"SELECT r.ConfigurationJson,p.Version,p.Retired FROM [workerdock].ConnectionProfiles p JOIN [workerdock].ConnectionProfileRevisions r ON r.ProfileRevisionId=p.CurrentRevisionId WHERE p.ProfileId=@id");Add(command,"@id",SqlDbType.UniqueIdentifier,id);
        await using var reader=await command.ExecuteReaderAsync(ct);return await reader.ReadAsync(ct)?Read(reader):null;
    }
    private static ProfileEditorDto Read(DbDataReader reader)=>new(JsonSerializer.Deserialize<ConnectionProfileRevision>(reader.GetString(0))!,reader.GetFieldValue<byte[]>(1),reader.GetBoolean(2));
    internal static async ValueTask<CommandReceipt> ReceiptAsync(DbConnection c,DbTransaction t,Guid target,string kind,ActorContext actor,CancellationToken ct,string state="Applied",long? beforeRevision=null,long? afterRevision=null)
    {
        var receipt=new CommandReceipt(Guid.NewGuid(),target,state);
        await using var command=Command(c,t,"INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@command,@target,@kind,@state,SYSUTCDATETIME()); INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(NEWID(),@kind,@subject,@mode,@correlation,SYSUTCDATETIME(),@details)");
        Add(command,"@command",SqlDbType.UniqueIdentifier,receipt.CommandId);Add(command,"@target",SqlDbType.UniqueIdentifier,target);Add(command,"@kind",SqlDbType.NVarChar,kind,40);Add(command,"@state",SqlDbType.NVarChar,state,20);Add(command,"@subject",SqlDbType.NVarChar,actor.Subject,300);Add(command,"@mode",SqlDbType.NVarChar,actor.AuthenticationMode,40);Add(command,"@correlation",SqlDbType.UniqueIdentifier,actor.CorrelationId);Add(command,"@details",SqlDbType.NVarChar,JsonSerializer.Serialize(new { targetId=target,beforeRevision,afterRevision,outcome=state }),4000);await command.ExecuteNonQueryAsync(ct);return receipt;
    }
}
