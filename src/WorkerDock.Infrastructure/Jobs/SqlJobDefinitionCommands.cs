using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Scheduling;
using WorkerDock.Application.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Scheduling;

namespace WorkerDock.Infrastructure.Jobs;

public sealed class SqlJobDefinitionCommands(TenantDatabaseOptions options, ITemplateCatalog? templates,
    IScheduleEvaluator evaluator, TimeProvider clock, ITemplateAuthoringService? authoring = null,
    IScheduleRulesFingerprint? rulesFingerprint = null) : IJobDefinitionCommands
{
    private readonly IScheduleRulesFingerprint rules = rulesFingerprint ?? new SystemScheduleRulesFingerprint();

    public async ValueTask<CommandReceipt> SaveDefinitionAsync(Guid jobId, JobRevision proposed, ScheduleEditorInput? schedule,
        MissedWorkPolicy? missedPolicy, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor); JobCommandValidation.ValidateRevision(proposed);
        if (jobId == Guid.Empty || proposed.JobId != jobId) throw new WorkerDockException("ValidationFailed", "jobId");
        if (expectedVersion is not { Length: 8 }) throw new WorkerDockException("ValidationFailed", "version");
        if ((schedule is null) != (missedPolicy is null)) throw new WorkerDockException("ValidationFailed", "missedPolicy");
        var definition = schedule is null ? null : ScheduleInputConverter.ConvertInput(schedule);
        if (definition is not null)
        {
            if (evaluator.Validate(definition).Count != 0) throw new WorkerDockException("ValidationFailed", "schedule");
            try { missedPolicy!.Validate(); }
            catch (ArgumentException) { throw new WorkerDockException("ValidationFailed", "missedPolicy"); }
        }
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await using (var instance = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings"))
                    if ((Guid?)await instance.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                await using var read = Command(connection, transaction, "SELECT j.CurrentRevision,j.Version,j.Archived,j.Enabled,r.TemplateId,r.TemplateVersion FROM [workerdock].Jobs j WITH(UPDLOCK,HOLDLOCK) JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision WHERE j.JobId=@job");
                Add(read, "@job", SqlDbType.UniqueIdentifier, jobId);
                await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) throw new WorkerDockException("JobNotFound");
                var number = checked(row.GetInt64(0) + 1); var version = row.GetFieldValue<byte[]>(1); var archived = row.GetBoolean(2); var enabled = row.GetBoolean(3);
                var templateId = row.GetString(4); var templateVersion = row.GetString(5); await row.CloseAsync();
                if (!version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
                if (archived) throw new WorkerDockException("JobArchived");
                if (templateId != proposed.TemplateId || templateVersion != proposed.TemplateVersion) throw new WorkerDockException("ValidationFailed", "templateId");
                await TemplateJobValidation.RequireRegisteredAsync(templates, proposed.TemplateId, proposed.TemplateVersion, token);
                await TemplateJobValidation.ValidateBindingsAsync(templates, proposed, token);
                await using (var catalog = Command(connection, transaction, "SELECT Enabled FROM [workerdock].TemplateCatalog WHERE TemplateId=@template AND TemplateVersion=@version"))
                {
                    Add(catalog, "@template", SqlDbType.NVarChar, proposed.TemplateId, 100); Add(catalog, "@version", SqlDbType.NVarChar, proposed.TemplateVersion, 40);
                    if (await catalog.ExecuteScalarAsync(token) is not true) throw new WorkerDockException("UnknownTemplate", "templateId");
                }
                if (enabled)
                {
                    await TemplateJobValidation.ValidateAsync(templates, proposed, token, authoring, definition is null ? "Manual" : "Scheduled");
                    if (proposed.ProfileId is { } profileId)
                    {
                        await using var profile = Command(connection, transaction, "SELECT Retired,CurrentRevisionId FROM [workerdock].ConnectionProfiles WITH(UPDLOCK,HOLDLOCK) WHERE ProfileId=@id");
                        Add(profile, "@id", SqlDbType.UniqueIdentifier, profileId); await using var profileRow = await profile.ExecuteReaderAsync(token);
                        if (!await profileRow.ReadAsync(token) || profileRow.GetBoolean(0) || profileRow.IsDBNull(1)) throw new WorkerDockException("ProfileUnavailable");
                    }
                }
                var revision = JobCommandValidation.Freeze(proposed with { Revision = number });
                await using (var insert = Command(connection, transaction, "INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc,DynamicValuesJson,HideFailureDetails) VALUES(@job,@revision,@name,@template,@templateVersion,@configuration,@profile,@policy,SYSUTCDATETIME(),@dynamic,@hideFailureDetails)"))
                {
                    Add(insert, "@job", SqlDbType.UniqueIdentifier, jobId); Add(insert, "@revision", SqlDbType.BigInt, number);
                    Add(insert, "@name", SqlDbType.NVarChar, revision.Name, 200); Add(insert, "@template", SqlDbType.NVarChar, revision.TemplateId, 100); Add(insert, "@templateVersion", SqlDbType.NVarChar, revision.TemplateVersion, 40);
                    Add(insert, "@configuration", SqlDbType.NVarChar, revision.Configuration.GetRawText(), -1); Add(insert, "@profile", SqlDbType.UniqueIdentifier, revision.ProfileId ?? (object)DBNull.Value);
                    Add(insert, "@policy", SqlDbType.NVarChar, JsonSerializer.Serialize(revision.Policy), -1); Add(insert, "@dynamic", SqlDbType.NVarChar, revision.DynamicValues?.GetRawText() ?? (object)DBNull.Value, -1);
                    Add(insert, "@hideFailureDetails", SqlDbType.Bit, revision.HideFailureDetails);
                    await insert.ExecuteNonQueryAsync(token);
                }
                await SaveScheduleAsync(connection, transaction, jobId, definition, missedPolicy, token);
                await using (var update = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=@revision WHERE JobId=@job AND Version=@version"))
                {
                    Add(update, "@job", SqlDbType.UniqueIdentifier, jobId); Add(update, "@revision", SqlDbType.BigInt, number); Add(update, "@version", SqlDbType.Binary, version, 8);
                    if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
                }
                var receipt = new CommandReceipt(Guid.NewGuid(), jobId, "Applied");
                await using (var command = Command(connection, transaction, "INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@id,@job,N'JobDefinitionSave',N'Applied',SYSUTCDATETIME())"))
                {
                    Add(command, "@id", SqlDbType.UniqueIdentifier, receipt.CommandId); Add(command, "@job", SqlDbType.UniqueIdentifier, jobId); await command.ExecuteNonQueryAsync(token);
                }
                await using (var audit = Command(connection, transaction, "INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(@id,N'JobDefinitionSaved',@subject,@mode,@correlation,SYSUTCDATETIME(),@details)"))
                {
                    Add(audit, "@id", SqlDbType.UniqueIdentifier, Guid.NewGuid()); Add(audit, "@subject", SqlDbType.NVarChar, actor.Subject, 300); Add(audit, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40); Add(audit, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
                    Add(audit, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(new { targetId = jobId, beforeRevision = number - 1, afterRevision = number, outcome = "Applied" }), 4000); await audit.ExecuteNonQueryAsync(token);
                }
                return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask SaveScheduleAsync(DbConnection connection, DbTransaction transaction, Guid jobId, ScheduleDefinition? proposed, MissedWorkPolicy? policy, CancellationToken ct)
    {
        await using var read = Command(connection, transaction, "SELECT ScheduleId,Revision,Kind,Cron,ZoneId,OneTimeUtc,Removed,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs FROM [workerdock].Schedules WITH(UPDLOCK,HOLDLOCK) WHERE JobId=@job");
        Add(read, "@job", SqlDbType.UniqueIdentifier, jobId); await using var row = await read.ExecuteReaderAsync(ct);
        var exists = await row.ReadAsync(ct); var id = exists ? row.GetGuid(0) : Guid.NewGuid(); var number = exists ? checked(row.GetInt64(1) + 1) : 1;
        var removed = exists && row.GetBoolean(6);
        var unchanged = proposed is not null && exists && !removed && row.GetString(2) == proposed.Kind && (row.IsDBNull(3) ? null : row.GetString(3)) == proposed.Cron && row.GetString(4) == proposed.ZoneId
            && (row.IsDBNull(5) ? (DateTime?)null : row.GetDateTime(5)) == proposed.OneTimeUtc?.UtcDateTime
            && row.GetString(7) == policy!.Mode && row.GetInt64(8) == checked((long)policy.LatenessTolerance.TotalMilliseconds)
            && row.GetInt32(9) == policy.CatchUpCount && row.GetInt64(10) == checked((long)policy.CatchUpAge.TotalMilliseconds);
        await row.CloseAsync();
        if (unchanged || (proposed is null && (!exists || removed))) return;
        if (proposed is null)
        {
            await using var remove = Command(connection, transaction, "UPDATE [workerdock].Schedules SET Removed=1,Revision=Revision+1,CatchUpRecoveryThroughUtc=NULL,CatchUpRecoveryCreated=0 WHERE JobId=@job AND Removed=0");
            Add(remove, "@job", SqlDbType.UniqueIdentifier, jobId); await remove.ExecuteNonQueryAsync(ct); return;
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var sql = exists
            ? "UPDATE [workerdock].Schedules SET Revision=@revision,Kind=@kind,Cron=@cron,ZoneId=@zone,OneTimeUtc=@once,ActivatedUtc=@now,CursorUtc=@now,Removed=0,CatchUpRecoveryThroughUtc=NULL,CatchUpRecoveryCreated=0,MissedMode=@mode,LatenessToleranceMs=@tolerance,CatchUpCount=@count,CatchUpAgeMs=@age,ZoneRulesFingerprint=@fingerprint WHERE JobId=@job"
            : "INSERT [workerdock].Schedules(ScheduleId,JobId,Revision,Kind,Cron,ZoneId,OneTimeUtc,ActivatedUtc,CursorUtc,Paused,Removed,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs,ZoneRulesFingerprint) VALUES(@id,@job,@revision,@kind,@cron,@zone,@once,@now,@now,0,0,@mode,@tolerance,@count,@age,@fingerprint)";
        await using var write = Command(connection, transaction, sql);
        Add(write, "@id", SqlDbType.UniqueIdentifier, id); Add(write, "@job", SqlDbType.UniqueIdentifier, jobId); Add(write, "@revision", SqlDbType.BigInt, number);
        Add(write, "@kind", SqlDbType.NVarChar, proposed.Kind, 16); Add(write, "@cron", SqlDbType.NVarChar, proposed.Cron ?? (object)DBNull.Value, 120); Add(write, "@zone", SqlDbType.NVarChar, proposed.ZoneId, 100);
        Add(write, "@once", SqlDbType.DateTime2, proposed.OneTimeUtc?.UtcDateTime ?? (object)DBNull.Value); Add(write, "@now", SqlDbType.DateTime2, now);
        Add(write, "@mode", SqlDbType.NVarChar, policy!.Mode, 16); Add(write, "@tolerance", SqlDbType.BigInt, checked((long)policy.LatenessTolerance.TotalMilliseconds)); Add(write, "@count", SqlDbType.Int, policy.CatchUpCount); Add(write, "@age", SqlDbType.BigInt, checked((long)policy.CatchUpAge.TotalMilliseconds));
        Add(write, "@fingerprint", SqlDbType.Char, rules.GetFingerprint(proposed.ZoneId), 64); await write.ExecuteNonQueryAsync(ct);
    }
    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Add(DbCommand command, string name, SqlDbType type, object value, int size = 0)
    { var parameter = new SqlParameter(name, type) { Value = value }; if (size != 0) parameter.Size = size; command.Parameters.Add(parameter); }
}

public sealed class UnavailableJobDefinitionCommands : IJobDefinitionCommands
{
    public ValueTask<CommandReceipt> SaveDefinitionAsync(Guid jobId, JobRevision proposed, ScheduleEditorInput? schedule, MissedWorkPolicy? missedPolicy, byte[] expectedVersion, ActorContext actor, CancellationToken ct) => ValueTask.FromException<CommandReceipt>(new WorkerDockException("PersistenceUnavailable"));
}
