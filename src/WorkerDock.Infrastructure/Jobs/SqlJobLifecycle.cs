using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Scheduling;
using WorkerDock.Application.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Jobs;

public sealed class SqlJobLifecycle(TenantDatabaseOptions options, ITemplateCatalog? templates, IScheduleEvaluator evaluator, TimeProvider clock) : IJobLifecycle, IJobDuplicator
{
    public async ValueTask<CommandReceipt> ApplyAsync(Guid jobId, string action, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        if (jobId == Guid.Empty || expectedVersion is not { Length: 8 } || action is not ("PauseSchedule" or "ResumeSchedule" or "Disable" or "Enable" or "EnableManual" or "Archive"))
            throw new WorkerDockException("ValidationFailed", "action");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await using (var instance = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings"))
                    if ((Guid?)await instance.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                await using var read = Command(connection, transaction, "SELECT j.CurrentRevision,j.Version,j.Enabled,j.Archived,r.Name,r.TemplateId,r.TemplateVersion,r.ConfigurationJson,r.ProfileId,r.PolicyJson FROM [workerdock].Jobs j WITH(UPDLOCK,HOLDLOCK) JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision WHERE j.JobId=@job");
                Add(read, "@job", SqlDbType.UniqueIdentifier, jobId); await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) throw new WorkerDockException("JobNotFound");
                var revision = row.GetInt64(0); var version = row.GetFieldValue<byte[]>(1); var enabled = row.GetBoolean(2); var archived = row.GetBoolean(3);
                var current = new JobRevision(jobId, revision, row.GetString(4), row.GetString(5), row.GetString(6), JsonDocument.Parse(row.GetString(7)).RootElement.Clone(), row.IsDBNull(8) ? null : row.GetGuid(8), JsonSerializer.Deserialize<ExecutionPolicy>(row.GetString(9))!);
                await row.CloseAsync();
                if (!version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
                if (archived && action != "Enable") throw new WorkerDockException("JobArchived");
                if (action == "Enable" && enabled) return await NoOpReceiptAsync(connection, transaction, jobId, action, token);
                if (action is "Enable" or "EnableManual")
                {
                    await ValidateForEnableAsync(connection, transaction, current, token, action == "EnableManual");
                    if (action == "EnableManual" && enabled) return await NoOpReceiptAsync(connection, transaction, jobId, action, token);
                }
                var now = clock.GetUtcNow().UtcDateTime;
                switch (action)
                {
                    case "PauseSchedule": await UpdateSchedulePauseAsync(connection, transaction, jobId, true, now, token); break;
                    case "ResumeSchedule": await UpdateSchedulePauseAsync(connection, transaction, jobId, false, now, token); break;
                    case "Disable":
                        await using (var update = Command(connection, transaction, "UPDATE [workerdock].Jobs SET Enabled=0,DisabledAtUtc=@now WHERE JobId=@job AND Version=@version"))
                        { Add(update, "@job", SqlDbType.UniqueIdentifier, jobId); Add(update, "@version", SqlDbType.Binary, version, 8); Add(update, "@now", SqlDbType.DateTime2, now); if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion"); }
                        await AdvanceScheduleAsync(connection, transaction, jobId, now, false, token);
                        break;
                    case "Enable":
                    case "EnableManual":
                        await using (var update = Command(connection, transaction, "UPDATE [workerdock].Jobs SET Enabled=1,Archived=0,ActivationBoundaryUtc=@now,DisabledAtUtc=NULL WHERE JobId=@job AND Version=@version AND Archived=0"))
                        { Add(update, "@job", SqlDbType.UniqueIdentifier, jobId); Add(update, "@version", SqlDbType.Binary, version, 8); Add(update, "@now", SqlDbType.DateTime2, now); if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion"); }
                        await JobAdmission.AdmitAsync(connection, transaction, jobId, token);
                        await ResetScheduleBoundaryAsync(connection, transaction, jobId, now, token);
                        break;
                    case "Archive":
                        await using (var update = Command(connection, transaction, "UPDATE [workerdock].Jobs SET Enabled=0,Archived=1,DisabledAtUtc=@now WHERE JobId=@job AND Version=@version"))
                        { Add(update, "@job", SqlDbType.UniqueIdentifier, jobId); Add(update, "@version", SqlDbType.Binary, version, 8); Add(update, "@now", SqlDbType.DateTime2, now); if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion"); }
                        await RemoveScheduleAsync(connection, transaction, jobId, token);
                        break;
                }
                if (action is "PauseSchedule" or "ResumeSchedule")
                {
                    await using var etag = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=CurrentRevision WHERE JobId=@job AND Version=@version");
                    Add(etag, "@job", SqlDbType.UniqueIdentifier, jobId); Add(etag, "@version", SqlDbType.Binary, version, 8);
                    if (await etag.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
                }
                await using (var bump = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=CurrentRevision WHERE JobId=@job AND Version<>@version"))
                { Add(bump,"@job",SqlDbType.UniqueIdentifier,jobId); Add(bump,"@version",SqlDbType.Binary,version,8); if(await bump.ExecuteNonQueryAsync(token)!=1)throw new WorkerDockException("StaleVersion"); }
                var receipt = new CommandReceipt(Guid.NewGuid(), jobId, "Applied");
                await using (var command = Command(connection, transaction, "INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@id,@target,@kind,@state,SYSUTCDATETIME())"))
                { Add(command, "@id", SqlDbType.UniqueIdentifier, receipt.CommandId); Add(command, "@target", SqlDbType.UniqueIdentifier, jobId); Add(command, "@kind", SqlDbType.NVarChar, action, 40); Add(command, "@state", SqlDbType.NVarChar, receipt.State, 20); await command.ExecuteNonQueryAsync(token); }
                await AuditAsync(connection, transaction, action, jobId, revision, actor, token);
                return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<CommandReceipt> DuplicateAsync(Guid sourceJobId, Guid targetJobId, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        if (sourceJobId == Guid.Empty || targetJobId == Guid.Empty || expectedVersion is not { Length: 8 } || sourceJobId == targetJobId) throw new WorkerDockException("ValidationFailed", "jobId");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await using (var instance=Command(connection,transaction,"SELECT InstanceId FROM [workerdock].TenantSettings"))
                    if((Guid?)await instance.ExecuteScalarAsync(token)!=options.InstanceId)throw new WorkerDockException("TenantInstanceMismatch");
                await using var read = Command(connection, transaction, "SELECT j.Version,r.Name,r.TemplateId,r.TemplateVersion,r.ConfigurationJson,r.ProfileId,r.PolicyJson FROM [workerdock].Jobs j WITH(UPDLOCK,HOLDLOCK) JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision WHERE j.JobId=@job AND j.Archived=0");
                Add(read, "@job", SqlDbType.UniqueIdentifier, sourceJobId); await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) throw new WorkerDockException("JobNotFound");
                var version = row.GetFieldValue<byte[]>(0); var name = row.GetString(1); var template = row.GetString(2); var templateVersion = row.GetString(3); var configuration = row.GetString(4); Guid? profile = row.IsDBNull(5) ? null : row.GetGuid(5); var policy = row.GetString(6); await row.CloseAsync();
                if (!version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
                await using (var insert = Command(connection, transaction, "INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES(@target,1,0,0,1)")) { Add(insert, "@target", SqlDbType.UniqueIdentifier, targetJobId); await insert.ExecuteNonQueryAsync(token); }
                await using (var revision = Command(connection, transaction, "INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc) VALUES(@target,1,@name,@template,@version,@configuration,@profile,@policy,SYSUTCDATETIME())"))
                { Add(revision,"@target",SqlDbType.UniqueIdentifier,targetJobId); Add(revision,"@name",SqlDbType.NVarChar,name+" (copy)",200); Add(revision,"@template",SqlDbType.NVarChar,template,100); Add(revision,"@version",SqlDbType.NVarChar,templateVersion,40); Add(revision,"@configuration",SqlDbType.NVarChar,configuration,-1); Add(revision,"@profile",SqlDbType.UniqueIdentifier,profile??(object)DBNull.Value); Add(revision,"@policy",SqlDbType.NVarChar,policy,-1); await revision.ExecuteNonQueryAsync(token); }
                var receipt = new CommandReceipt(Guid.NewGuid(), targetJobId, "Applied");
                await using (var command = Command(connection, transaction, "INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@id,@target,N'Duplicate',N'Applied',SYSUTCDATETIME())")) { Add(command,"@id",SqlDbType.UniqueIdentifier,receipt.CommandId); Add(command,"@target",SqlDbType.UniqueIdentifier,targetJobId); await command.ExecuteNonQueryAsync(token); }
                await AuditAsync(connection, transaction, "JobDuplicated", targetJobId, 0, actor, token); return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask ValidateForEnableAsync(DbConnection c, DbTransaction t, JobRevision job, CancellationToken ct, bool requireNoSchedule)
    {
        JobCommandValidation.ValidateRevision(job); await TemplateJobValidation.ValidateAsync(templates, job, ct);
        if (job.ProfileId is { } id)
        {
            await using var profile = Command(c, t, "SELECT Retired,CurrentRevisionId FROM [workerdock].ConnectionProfiles WITH(UPDLOCK,HOLDLOCK) WHERE ProfileId=@id"); Add(profile, "@id", SqlDbType.UniqueIdentifier, id); await using var row = await profile.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct) || row.GetBoolean(0) || row.IsDBNull(1)) throw new WorkerDockException("ProfileUnavailable");
            await row.CloseAsync();
        }
        await using var schedule = Command(c, t, "SELECT Kind,Cron,ZoneId,OneTimeUtc,ActivatedUtc,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs FROM [workerdock].Schedules WITH(UPDLOCK,HOLDLOCK) WHERE JobId=@job AND Removed=0"); Add(schedule, "@job", SqlDbType.UniqueIdentifier, job.JobId); await using var scheduleRow = await schedule.ExecuteReaderAsync(ct);
        if (await scheduleRow.ReadAsync(ct))
        {
            if (requireNoSchedule) throw new WorkerDockException("ScheduleExists");
            var definition = new ScheduleDefinition(Guid.NewGuid(), 1, scheduleRow.GetString(0), scheduleRow.IsDBNull(1) ? null : scheduleRow.GetString(1), scheduleRow.GetString(2), scheduleRow.IsDBNull(3) ? null : new DateTimeOffset(DateTime.SpecifyKind(scheduleRow.GetDateTime(3), DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(scheduleRow.GetDateTime(4), DateTimeKind.Utc)));
            var policy = new MissedWorkPolicy(scheduleRow.GetString(5), TimeSpan.FromMilliseconds(scheduleRow.GetInt64(6)), scheduleRow.GetInt32(7), TimeSpan.FromMilliseconds(scheduleRow.GetInt64(8))); policy.Validate();
            if (evaluator.Validate(definition).Count != 0) throw new WorkerDockException("ValidationFailed", "schedule");
        }
    }
    private static async ValueTask<CommandReceipt> NoOpReceiptAsync(DbConnection c, DbTransaction t, Guid jobId, string action, CancellationToken ct)
    {
        var receipt = new CommandReceipt(Guid.NewGuid(), jobId, "Applied");
        await using var command = Command(c, t, "INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@id,@target,@kind,@state,SYSUTCDATETIME())");
        Add(command, "@id", SqlDbType.UniqueIdentifier, receipt.CommandId); Add(command, "@target", SqlDbType.UniqueIdentifier, jobId); Add(command, "@kind", SqlDbType.NVarChar, action, 40); Add(command, "@state", SqlDbType.NVarChar, receipt.State, 20);
        await command.ExecuteNonQueryAsync(ct);
        return receipt;
    }
    private static async ValueTask UpdateSchedulePauseAsync(DbConnection c, DbTransaction t, Guid job, bool pause, DateTime now, CancellationToken ct)
    {
        await using var command=Command(c,t,pause?"UPDATE [workerdock].Schedules SET Paused=1,PausedAtUtc=@now,Revision=Revision+1 WHERE JobId=@job AND Removed=0":"UPDATE [workerdock].Schedules SET Paused=0,PausedAtUtc=NULL,CursorUtc=@now,Revision=Revision+1 WHERE JobId=@job AND Removed=0"); Add(command,"@job",SqlDbType.UniqueIdentifier,job); Add(command,"@now",SqlDbType.DateTime2,now); if(await command.ExecuteNonQueryAsync(ct)!=1)throw new WorkerDockException("ScheduleNotFound");
        var id=(Guid)await ScalarAsync(c,t,"SELECT ScheduleId FROM [workerdock].Schedules WHERE JobId=@job",job,ct); var revision=(long)await ScalarAsync(c,t,"SELECT Revision FROM [workerdock].Schedules WHERE JobId=@job",job,ct); await OutboxAsync(c,t,id,revision,ct);
    }
    private static async ValueTask ResetScheduleBoundaryAsync(DbConnection c,DbTransaction t,Guid job,DateTime now,CancellationToken ct)
    { await AdvanceScheduleAsync(c,t,job,now,true,ct); }
    private static async ValueTask RemoveScheduleAsync(DbConnection c,DbTransaction t,Guid job,CancellationToken ct)
    { await using var command=Command(c,t,"UPDATE [workerdock].Schedules SET Removed=1,Revision=Revision+1 WHERE JobId=@job AND Removed=0"); Add(command,"@job",SqlDbType.UniqueIdentifier,job); if(await command.ExecuteNonQueryAsync(ct)==1) { var id=(Guid)await ScalarAsync(c,t,"SELECT ScheduleId FROM [workerdock].Schedules WHERE JobId=@job",job,ct); var revision=(long)await ScalarAsync(c,t,"SELECT Revision FROM [workerdock].Schedules WHERE JobId=@job",job,ct); await OutboxAsync(c,t,id,revision,ct); } }
    private static async ValueTask AdvanceScheduleAsync(DbConnection c,DbTransaction t,Guid job,DateTime now,bool activate,CancellationToken ct)
    { await using var command=Command(c,t,activate?"UPDATE [workerdock].Schedules SET ActivatedUtc=@now,CursorUtc=@now,Revision=Revision+1 WHERE JobId=@job AND Removed=0":"UPDATE [workerdock].Schedules SET CursorUtc=@now,Revision=Revision+1 WHERE JobId=@job AND Removed=0"); Add(command,"@job",SqlDbType.UniqueIdentifier,job); Add(command,"@now",SqlDbType.DateTime2,now); if(await command.ExecuteNonQueryAsync(ct)==1) { var id=(Guid)await ScalarAsync(c,t,"SELECT ScheduleId FROM [workerdock].Schedules WHERE JobId=@job",job,ct); var revision=(long)await ScalarAsync(c,t,"SELECT Revision FROM [workerdock].Schedules WHERE JobId=@job",job,ct); await OutboxAsync(c,t,id,revision,ct); } }
    private static async ValueTask OutboxAsync(DbConnection c,DbTransaction t,Guid id,long revision,CancellationToken ct)
    { var digest=WorkerDock.Application.Dispatch.ProjectionEnvelope.ScheduleDigest(id,revision); var payload=JsonSerializer.Serialize(new { scheduleId=id,scheduleRevision=revision }); await using var cmd=Command(c,t,"INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState) VALUES(@event,N'ScheduleProjection',@payload,SYSUTCDATETIME(),SYSUTCDATETIME(),1,@id,@revision,@digest,N'Pending')"); Add(cmd,"@event",SqlDbType.UniqueIdentifier,Guid.NewGuid()); Add(cmd,"@payload",SqlDbType.NVarChar,payload,-1); Add(cmd,"@id",SqlDbType.UniqueIdentifier,id); Add(cmd,"@revision",SqlDbType.BigInt,revision); Add(cmd,"@digest",SqlDbType.Char,digest,64); await cmd.ExecuteNonQueryAsync(ct); }
    private static async ValueTask<object> ScalarAsync(DbConnection c,DbTransaction t,string sql,Guid job,CancellationToken ct) { await using var cmd=Command(c,t,sql); Add(cmd,"@job",SqlDbType.UniqueIdentifier,job); return (await cmd.ExecuteScalarAsync(ct))!; }
    private static async ValueTask AuditAsync(DbConnection c,DbTransaction t,string action,Guid target,long revision,ActorContext actor,CancellationToken ct)
    { await using var cmd=Command(c,t,"INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(@id,@kind,@subject,@mode,@correlation,SYSUTCDATETIME(),@details)"); Add(cmd,"@id",SqlDbType.UniqueIdentifier,Guid.NewGuid()); Add(cmd,"@kind",SqlDbType.NVarChar,"Job"+action,100); Add(cmd,"@subject",SqlDbType.NVarChar,actor.Subject,300); Add(cmd,"@mode",SqlDbType.NVarChar,actor.AuthenticationMode,40); Add(cmd,"@correlation",SqlDbType.UniqueIdentifier,actor.CorrelationId); Add(cmd,"@details",SqlDbType.NVarChar,JsonSerializer.Serialize(new { targetId=target,revision,outcome="Applied" }),4000); await cmd.ExecuteNonQueryAsync(ct); }
    private static DbCommand Command(DbConnection c,DbTransaction t,string sql) { var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; return cmd; }
    private static void Add(DbCommand c,string n,SqlDbType type,object value,int size=0) { var p=new SqlParameter(n,type){Value=value}; if(size!=0)p.Size=size; c.Parameters.Add(p); }
}
