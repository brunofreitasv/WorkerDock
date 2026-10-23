using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.Infrastructure.Jobs;

public sealed class SqlJobCommands(TenantDatabaseOptions options, ITemplateCatalog? templateCatalog = null, ITemplateAuthoringService? authoring = null) : IJobCommands
{
    public async ValueTask<CommandReceipt> SaveAsync(JobRevision proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        JobCommandValidation.ValidateRevision(proposed);
        await TemplateJobValidation.RequireRegisteredAsync(templateCatalog, proposed.TemplateId, proposed.TemplateVersion, ct);
        await TemplateJobValidation.ValidateBindingsAsync(templateCatalog, proposed, ct);
        if (expectedVersion is not null && expectedVersion.Length != 8)
            throw new WorkerDockException("ValidationFailed", "version");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection, transaction, token);
                await using var catalog = Command(connection, transaction,
                    "SELECT Enabled FROM [workerdock].TemplateCatalog WHERE TemplateId=@template AND TemplateVersion=@version");
                Add(catalog, "@template", SqlDbType.NVarChar, proposed.TemplateId, 100);
                Add(catalog, "@version", SqlDbType.NVarChar, proposed.TemplateVersion, 40);
                if (await catalog.ExecuteScalarAsync(token) is not bool enabled || !enabled)
                    throw new WorkerDockException("UnknownTemplate", "templateId");

                await using var read = Command(connection, transaction,
                    "SELECT CurrentRevision,Version FROM [workerdock].Jobs WHERE JobId=@jobId");
                Add(read, "@jobId", SqlDbType.UniqueIdentifier, proposed.JobId);
                await using var reader = await read.ExecuteReaderAsync(token);
                long? currentRevision = null;
                byte[]? currentVersion = null;
                if (await reader.ReadAsync(token))
                {
                    currentRevision = reader.GetInt64(0);
                    currentVersion = reader.GetFieldValue<byte[]>(1);
                }
                await reader.CloseAsync();
                if (expectedVersion is null && currentVersion is not null) throw new WorkerDockException("JobExists");
                if (expectedVersion is not null && currentVersion is null) throw new WorkerDockException("JobNotFound");
                if (currentVersion is not null && !currentVersion.AsSpan().SequenceEqual(expectedVersion))
                    throw new WorkerDockException("StaleVersion");
                var number = (currentRevision ?? 0) + 1;
                var revision = JobCommandValidation.Freeze(proposed with { Revision = number });
                if (currentVersion is null)
                {
                    await using var insertJob = Command(connection, transaction,
                        "INSERT INTO [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES (@id,NULL,0,0,1)");
                    Add(insertJob, "@id", SqlDbType.UniqueIdentifier, proposed.JobId);
                    await insertJob.ExecuteNonQueryAsync(token);
                }
                await using var insertRevision = Command(connection, transaction,
                    "INSERT INTO [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc,DynamicValuesJson) VALUES (@job,@rev,@name,@template,@version,@config,@profile,@policy,SYSUTCDATETIME(),@dynamicValues)");
                Add(insertRevision, "@job", SqlDbType.UniqueIdentifier, proposed.JobId);
                Add(insertRevision, "@rev", SqlDbType.BigInt, number);
                Add(insertRevision, "@name", SqlDbType.NVarChar, proposed.Name, 200);
                Add(insertRevision, "@template", SqlDbType.NVarChar, proposed.TemplateId, 100);
                Add(insertRevision, "@version", SqlDbType.NVarChar, proposed.TemplateVersion, 40);
                Add(insertRevision, "@config", SqlDbType.NVarChar, revision.Configuration.GetRawText(), -1);
                Add(insertRevision, "@profile", SqlDbType.UniqueIdentifier, proposed.ProfileId ?? (object)DBNull.Value);
                Add(insertRevision, "@policy", SqlDbType.NVarChar, JsonSerializer.Serialize(revision.Policy), -1);
                Add(insertRevision, "@dynamicValues", SqlDbType.NVarChar, revision.DynamicValues is { } dynamicValues ? dynamicValues.GetRawText() : DBNull.Value, -1);
                await insertRevision.ExecuteNonQueryAsync(token);
                await using var update = Command(connection, transaction,
                    currentVersion is null
                        ? "UPDATE [workerdock].Jobs SET CurrentRevision=@rev WHERE JobId=@id"
                        : "UPDATE [workerdock].Jobs SET CurrentRevision=@rev WHERE JobId=@id AND Version=@expected");
                Add(update, "@rev", SqlDbType.BigInt, number);
                Add(update, "@id", SqlDbType.UniqueIdentifier, proposed.JobId);
                if (currentVersion is not null) Add(update, "@expected", SqlDbType.Binary, expectedVersion!, 8);
                if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
                var receipt = new CommandReceipt(Guid.NewGuid(), proposed.JobId, "Applied");
                await InsertCommandAsync(connection, transaction, receipt, "JobSave", token);
                await InsertAuditAsync(connection, transaction, "JobRevisionSaved", proposed.JobId,
                    currentRevision, number, "Applied", actor, token);
                return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<CommandReceipt> EnableManualAsync(Guid jobId, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        if(jobId==Guid.Empty || expectedVersion.Length!=8) throw new WorkerDockException("ValidationFailed","version");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection,transaction,token);
                await using var read=Command(connection,transaction,"SELECT j.CurrentRevision,j.Version,j.Enabled,j.Archived,r.Name,r.TemplateId,r.TemplateVersion,r.ConfigurationJson,r.ProfileId,r.PolicyJson,r.DynamicValuesJson FROM [workerdock].Jobs j JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision WHERE j.JobId=@id");
                Add(read,"@id",SqlDbType.UniqueIdentifier,jobId);
                await using var row=await read.ExecuteReaderAsync(token);
                if(!await row.ReadAsync(token)) throw new WorkerDockException("JobNotFound");
                var version=row.GetFieldValue<byte[]>(1);
                if(!version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
                if(row.GetBoolean(3)) throw new WorkerDockException("JobArchived");
                var isEnabled=row.GetBoolean(2);
                var revision=new JobRevision(jobId,row.GetInt64(0),row.GetString(4),row.GetString(5),row.GetString(6),JsonDocument.Parse(row.GetString(7)).RootElement.Clone(),row.IsDBNull(8)?null:row.GetGuid(8),JsonSerializer.Deserialize<ExecutionPolicy>(row.GetString(9))!,row.IsDBNull(10)?null:JsonDocument.Parse(row.GetString(10)).RootElement.Clone());
                await row.CloseAsync();
                await TemplateJobValidation.ValidateAsync(templateCatalog,revision,token,authoring, "Manual");
                if(revision.ProfileId is { } profileId)
                {
                    await using var profile=Command(connection,transaction,"SELECT Retired,CurrentRevisionId FROM [workerdock].ConnectionProfiles WHERE ProfileId=@id");
                    Add(profile,"@id",SqlDbType.UniqueIdentifier,profileId);
                    await using var profileRow=await profile.ExecuteReaderAsync(token);
                    if(!await profileRow.ReadAsync(token) || profileRow.GetBoolean(0) || profileRow.IsDBNull(1)) throw new WorkerDockException("ProfileUnavailable");
                    await profileRow.CloseAsync();
                }
                if(!isEnabled)
                {
                    await using var update=Command(connection,transaction,"UPDATE [workerdock].Jobs SET Enabled=1 WHERE JobId=@id AND Version=@version AND Enabled=0 AND Archived=0");
                    Add(update,"@id",SqlDbType.UniqueIdentifier,jobId); Add(update,"@version",SqlDbType.Binary,expectedVersion,8);
                    if(await update.ExecuteNonQueryAsync(token)!=1) throw new WorkerDockException("StaleVersion");
                    await InsertAuditAsync(connection,transaction,"JobEnabled",jobId,revision.Revision,revision.Revision,"Applied",actor,token);
                }
                var receipt=new CommandReceipt(Guid.NewGuid(),jobId,"Applied");
                await InsertCommandAsync(connection,transaction,receipt,"EnableManual",token);
                return receipt;
            },ct);
        }
        catch(SqlException){throw new WorkerDockException("PersistenceUnavailable");}
    }

    public ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ActorContext actor, CancellationToken ct) =>
        RunNowAsync(jobId, requestKey, new ManualRunOptions(), actor, ct);

    public async ValueTask<CommandReceipt> RunNowAsync(Guid jobId, string requestKey, ManualRunOptions runOptions, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateRequestKey(jobId, requestKey, actor);
        if (runOptions is null) throw new WorkerDockException("ValidationFailed", "referenceDateOverride");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection, transaction, token);
                await using var time = Command(connection, transaction, "SELECT SYSUTCDATETIME()");
                var now = (DateTime)(await time.ExecuteScalarAsync(token))!;
                var scopeHash = Hash(actor.AuthenticationMode + "|" + actor.Subject);
                var keyHash = Hash(requestKey);
                var requestHash = runOptions.ReferenceDateOverride is null ? Hash("RunNow|" + jobId.ToString("N")) : Hash("RunNow|" + jobId.ToString("N") + "|" + runOptions.ReferenceDateOverride.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                await using var dedup = Command(connection, transaction,
                    "SELECT ManualActorMode,ManualActorSubject,RequestKey,ManualRequestHash,ExecutionId,ManualCommandId,ManualResponseState,ManualRequestExpiresAtUtc FROM [workerdock].Executions WITH (UPDLOCK,HOLDLOCK) WHERE JobId=@job AND TriggerKind=N'Manual' AND ManualActorScopeHash=@scope AND ManualRequestKeyHash=@key");
                Add(dedup, "@job", SqlDbType.UniqueIdentifier, jobId);
                Add(dedup, "@scope", SqlDbType.Char, scopeHash, 64);
                Add(dedup, "@key", SqlDbType.Char, keyHash, 64);
                await using var found = await dedup.ExecuteReaderAsync(token);
                Guid? oldExecution = null;
                Guid? oldCommand = null;
                string? oldState = null;
                DateTime expires = default;
                if (await found.ReadAsync(token))
                {
                    if (found.GetString(0) != actor.AuthenticationMode || found.GetString(1) != actor.Subject ||
                        found.GetString(2) != requestKey || found.GetString(3) != requestHash)
                        throw new WorkerDockException("RequestKeyConflict");
                    oldExecution = found.GetGuid(4);
                    oldCommand = found.GetGuid(5);
                    oldState = found.GetString(6);
                    expires = found.GetDateTime(7);
                }
                await found.CloseAsync();
                if (oldExecution is not null && now < expires)
                    return new CommandReceipt(oldCommand!.Value, oldExecution.Value, oldState!);
                if (oldExecution is not null)
                {
                    await using var remove = Command(connection, transaction,
                        "UPDATE [workerdock].Executions SET ManualActorScopeHash=NULL,ManualRequestKeyHash=NULL,ManualRequestExpiresAtUtc=NULL WHERE JobId=@job AND TriggerKind=N'Manual' AND ManualActorScopeHash=@scope AND ManualRequestKeyHash=@key");
                    Add(remove, "@job", SqlDbType.UniqueIdentifier, jobId);
                    Add(remove, "@scope", SqlDbType.Char, scopeHash, 64);
                    Add(remove, "@key", SqlDbType.Char, keyHash, 64);
                    await remove.ExecuteNonQueryAsync(token);
                }
                await using var job = Command(connection, transaction,
                    "SELECT CurrentRevision,Enabled,Archived FROM [workerdock].Jobs WHERE JobId=@id");
                Add(job, "@id", SqlDbType.UniqueIdentifier, jobId);
                await using var jobReader = await job.ExecuteReaderAsync(token);
                if (!await jobReader.ReadAsync(token)) throw new WorkerDockException("JobNotFound");
                var revision = jobReader.GetInt64(0);
                var enabled = jobReader.GetBoolean(1);
                var archived = jobReader.GetBoolean(2);
                await jobReader.CloseAsync();
                if (archived) throw new WorkerDockException("JobArchived");
                if (!enabled) throw new WorkerDockException("JobDisabled");
                await using var profile = Command(connection, transaction,
                    "SELECT ProfileId FROM [workerdock].JobRevisions WHERE JobId=@job AND Revision=@rev");
                Add(profile, "@job", SqlDbType.UniqueIdentifier, jobId);
                Add(profile, "@rev", SqlDbType.BigInt, revision);
                Guid? profileRevisionId=null;
                if (await profile.ExecuteScalarAsync(token) is Guid profileId)
                {
                    await using var pinnedProfile=Command(connection,transaction,"SELECT CurrentRevisionId FROM [workerdock].ConnectionProfiles WHERE ProfileId=@id AND Retired=0");
                    Add(pinnedProfile,"@id",SqlDbType.UniqueIdentifier,profileId);
                    profileRevisionId=await pinnedProfile.ExecuteScalarAsync(token) as Guid?;
                    if(profileRevisionId is null)
                        throw new WorkerDockException("ProfileUnavailable");
                }

                await using var configuration = Command(connection, transaction,
                    "SELECT TemplateId,TemplateVersion,ConfigurationJson,DynamicValuesJson FROM [workerdock].JobRevisions WHERE JobId=@job AND Revision=@rev");
                Add(configuration, "@job", SqlDbType.UniqueIdentifier, jobId); Add(configuration, "@rev", SqlDbType.BigInt, revision);
                await using var pinned = await configuration.ExecuteReaderAsync(token); await pinned.ReadAsync(token);
                using var json = JsonDocument.Parse(pinned.GetString(2));
                using var dynamicJson = pinned.IsDBNull(3) ? null : JsonDocument.Parse(pinned.GetString(3));
                var dynamicPlan = dynamicJson is null ? WorkerDock.Contracts.Values.DynamicValueJson.EmptyPlan : WorkerDock.Contracts.Values.DynamicValueJson.Read(dynamicJson.RootElement);
                var descriptor = templateCatalog is null ? null : await templateCatalog.GetAsync(pinned.GetString(0), pinned.GetString(1), token);
                var capable = descriptor is not null && ContainsDynamicAnnotation(descriptor.ConfigurationSchema);
                if (runOptions.ReferenceDateOverride is not null && (!capable || pinned.GetString(1) == "1.0.0")) throw new WorkerDockException("ValidationFailed", "referenceDateOverride");
                if (dynamicPlan.Bindings.Any(binding => binding.Source == WorkerDock.Contracts.Values.DynamicValueSource.ScheduledTimestamp)) throw new WorkerDockException("ValidationFailed", "configuration.dynamicValues");
                var valueContext = capable ? WorkerDock.Infrastructure.Values.ExecutionValueContextFactory.Manual(dynamicPlan, new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)), runOptions.ReferenceDateOverride) : null;
                if (!WorkerDock.Contracts.Execution.ExecutionPayloadLimits.FitsJobConfiguration(pinned.GetString(0), pinned.GetString(1), json.RootElement, dynamicJson?.RootElement))
                    throw new WorkerDockException("ValidationFailed", "configuration");
                await TemplateJobValidation.ValidateAsync(templateCatalog,pinned.GetString(0),pinned.GetString(1),json.RootElement,token,authoring, dynamicJson is null ? null : WorkerDock.Contracts.Values.DynamicValueJson.Read(dynamicJson.RootElement), "Manual");
                await pinned.CloseAsync();

                var executionId = Guid.NewGuid();
                var receipt = new CommandReceipt(Guid.NewGuid(), executionId, "Pending");
                await using var execution = Command(connection, transaction,
                    "INSERT INTO [workerdock].Executions(ExecutionId,JobId,JobRevision,ProfileRevisionId,TriggerKind,RequestKey,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc,ValueContextJson,ManualActorScopeHash,ManualRequestKeyHash,ManualActorMode,ManualActorSubject,ManualRequestHash,ManualCommandId,ManualResponseState,ManualRequestExpiresAtUtc) VALUES (@execution,@job,@rev,@profileRevision,N'Manual',@key,@idempotency,N'Queued',N'Known',@now,@correlation,@now,@context,@scope,@keyHash,@mode,@subject,@hash,@command,@state,DATEADD(hour,24,@now))");
                Add(execution, "@execution", SqlDbType.UniqueIdentifier, executionId);
                Add(execution,"@profileRevision",SqlDbType.UniqueIdentifier,profileRevisionId ?? (object)DBNull.Value);
                Add(execution, "@job", SqlDbType.UniqueIdentifier, jobId);
                Add(execution, "@rev", SqlDbType.BigInt, revision);
                Add(execution, "@key", SqlDbType.NVarChar, requestKey, 200);
                Add(execution, "@idempotency", SqlDbType.NVarChar, executionId.ToString("N"), 100);
                Add(execution, "@now", SqlDbType.DateTime2, now);
                Add(execution, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
                Add(execution, "@context", SqlDbType.NVarChar, valueContext is null ? DBNull.Value : JsonSerializer.Serialize(valueContext), -1);
                Add(execution, "@scope", SqlDbType.Char, scopeHash, 64);
                Add(execution, "@keyHash", SqlDbType.Char, keyHash, 64);
                Add(execution, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
                Add(execution, "@subject", SqlDbType.NVarChar, actor.Subject, 300);
                Add(execution, "@hash", SqlDbType.Char, requestHash, 64);
                Add(execution, "@command", SqlDbType.UniqueIdentifier, receipt.CommandId);
                Add(execution, "@state", SqlDbType.NVarChar, receipt.State, 20);
                await execution.ExecuteNonQueryAsync(token);
                await InsertCommandAsync(connection, transaction, receipt, "ManualRun", token);
                await InsertAuditAsync(connection, transaction, "ManualRunAccepted", executionId,
                    revision, revision, "Accepted", actor, token);
                var payload = JsonSerializer.Serialize(new { executionId, jobRevision = revision });
                await using var outbox = Command(connection, transaction,
                    "INSERT INTO [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState) VALUES (@id,N'ExecutionDispatch',@payload,@now,@now,1,@execution,@revision,@digest,N'Pending')");
                Add(outbox, "@id", SqlDbType.UniqueIdentifier, Guid.NewGuid());
                Add(outbox, "@payload", SqlDbType.NVarChar, payload, -1);
                Add(outbox, "@now", SqlDbType.DateTime2, now);
                Add(outbox, "@execution", SqlDbType.UniqueIdentifier, executionId);
                Add(outbox, "@revision", SqlDbType.BigInt, revision);
                Add(outbox, "@digest", SqlDbType.Char, Hash("ExecutionDispatch|" + executionId.ToString("N") + "|" + revision), 64);
                await outbox.ExecuteNonQueryAsync(token);
                await WorkerDock.Infrastructure.Dispatch.JobAdmission.AdmitAsync(connection, transaction, jobId, token);
                return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<CommandReceipt> GetCommandAsync(Guid commandId, CancellationToken ct)
    {
        try
        {
            await new TenantSettingsService(options).GetAsync(ct);
            await using var context = new JobDbContext(options.ConnectionString);
            var row = await context.Commands.AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == commandId, ct);
            return row is null ? throw new WorkerDockException("CommandNotFound") : new(row.CommandId, row.TargetId, row.State);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<JobEditorDto> GetEditableAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await new TenantSettingsService(options).GetAsync(ct);
            await using var context = new JobDbContext(options.ConnectionString);
            var job = await context.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == jobId, ct);
            if (job is null) throw new WorkerDockException("JobNotFound");
            var row = await context.Revisions.AsNoTracking().SingleAsync(x => x.JobId == jobId && x.Revision == job.CurrentRevision, ct);
            var policy = JsonSerializer.Deserialize<ExecutionPolicy>(row.PolicyJson)
                ?? throw new WorkerDockException("PersistenceUnavailable");
            using var configuration = JsonDocument.Parse(row.ConfigurationJson);
            using var dynamicValues = row.DynamicValuesJson is null ? null : JsonDocument.Parse(row.DynamicValuesJson);
            var revision = new JobRevision(jobId, row.Revision, row.Name, row.TemplateId, row.TemplateVersion,
                configuration.RootElement.Clone(), row.ProfileId, policy, dynamicValues?.RootElement.Clone());
            return new(revision, job.Version, job.Enabled, job.Archived, job.ConcurrencyLimit);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask CheckInstanceAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId)
            throw new WorkerDockException("TenantInstanceMismatch");
    }

    private static async ValueTask InsertCommandAsync(DbConnection connection, DbTransaction transaction,
        CommandReceipt receipt, string kind, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "INSERT INTO [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES (@id,@target,@kind,@state,SYSUTCDATETIME())");
        Add(command, "@id", SqlDbType.UniqueIdentifier, receipt.CommandId);
        Add(command, "@target", SqlDbType.UniqueIdentifier, receipt.TargetId);
        Add(command, "@kind", SqlDbType.NVarChar, kind, 40);
        Add(command, "@state", SqlDbType.NVarChar, receipt.State, 20);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask InsertAuditAsync(DbConnection connection, DbTransaction transaction,
        string kind, Guid targetId, long? beforeRevision, long afterRevision, string outcome,
        ActorContext actor, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "INSERT INTO [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES (@id,@kind,@subject,@mode,@correlation,SYSUTCDATETIME(),@details)");
        Add(command, "@id", SqlDbType.UniqueIdentifier, Guid.NewGuid());
        Add(command, "@kind", SqlDbType.NVarChar, kind, 100);
        Add(command, "@subject", SqlDbType.NVarChar, actor.Subject, 300);
        Add(command, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
        Add(command, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
        Add(command, "@details", SqlDbType.NVarChar,
            JsonSerializer.Serialize(new { targetId, beforeRevision, afterRevision, outcome }), 4000);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static void Add(DbCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = new SqlParameter(name, type) { Value = value };
        if (size != 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool ContainsDynamicAnnotation(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.NameEquals("x-workerDock-dynamicValue") && property.Value.ValueKind == JsonValueKind.True || ContainsDynamicAnnotation(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsDynamicAnnotation),
        _ => false
    };
}
