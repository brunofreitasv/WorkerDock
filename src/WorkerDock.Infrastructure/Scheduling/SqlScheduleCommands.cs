using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Scheduling;

public sealed class SqlScheduleCommands(TenantDatabaseOptions options, IScheduleEvaluator evaluator, TimeProvider clock, IScheduleRulesFingerprint? rulesFingerprint = null) : IScheduleCommands, IScheduleProjectionStore, IScheduleProjectionMaintenance
{
    private readonly IScheduleRulesFingerprint rules = rulesFingerprint ?? new SystemScheduleRulesFingerprint();

    public async ValueTask<ScheduleProjectionState?> ReadProjectionAsync(Guid scheduleId, CancellationToken ct)
    {
        try
        {
            await using var connection=new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct); await CheckInstanceAsync(connection,null,ct);
            await using var command=connection.CreateCommand(); command.CommandText="SELECT s.Revision,s.Kind,s.Cron,s.ZoneId,s.OneTimeUtc,s.ActivatedUtc,s.Paused,s.Removed,s.ZoneRulesFingerprint,j.Enabled,j.Archived FROM [workerdock].Schedules s JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.ScheduleId=@id";
            command.Parameters.Add(new SqlParameter("@id",SqlDbType.UniqueIdentifier){Value=scheduleId}); await using var row=await command.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct))return null;
            var definition=new ScheduleDefinition(scheduleId,row.GetInt64(0),row.GetString(1),row.IsDBNull(2)?null:row.GetString(2),row.GetString(3),row.IsDBNull(4)?null:Utc(row.GetDateTime(4)),Utc(row.GetDateTime(5)));
            return new ScheduleProjectionState(definition,row.GetBoolean(9),row.GetBoolean(10),row.GetBoolean(6),row.GetBoolean(7),row.GetString(8));
        }
        catch(SqlException){throw new WorkerDockException("PersistenceUnavailable");}
    }
    public ValueTask<IReadOnlyList<DateTimeOffset>> PreviewAsync(ScheduleDefinition definition, DateTimeOffset afterUtc, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (afterUtc.Offset != TimeSpan.Zero || count is < 1 or > 100) throw new WorkerDockException("ValidationFailed", "count");
        if (evaluator.Validate(definition).Count != 0) throw new WorkerDockException("ValidationFailed", "schedule");
        return ValueTask.FromResult(evaluator.Next(definition, afterUtc, count));
    }

    public async ValueTask<ScheduleEditorDto> GetAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            await CheckInstanceAsync(connection, null, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT s.ScheduleId,s.Revision,s.Kind,s.Cron,s.ZoneId,s.OneTimeUtc,s.ActivatedUtc,s.MissedMode,s.LatenessToleranceMs,s.CatchUpCount,s.CatchUpAgeMs,s.Paused,j.Version FROM [workerdock].Schedules s JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.JobId=@job AND s.Removed=0";
            command.Parameters.Add(new SqlParameter("@job", SqlDbType.UniqueIdentifier) { Value = jobId });
            await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new WorkerDockException("ScheduleNotFound");
            var definition = new ScheduleDefinition(row.GetGuid(0), row.GetInt64(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3), row.GetString(4), row.IsDBNull(5) ? null : Utc(row.GetDateTime(5)), Utc(row.GetDateTime(6)));
            var policy = new MissedWorkPolicy(row.GetString(7), TimeSpan.FromMilliseconds(row.GetInt64(8)), row.GetInt32(9), TimeSpan.FromMilliseconds(row.GetInt64(10)));
            return new ScheduleEditorDto(jobId, definition, policy, row.GetFieldValue<byte[]>(12), row.GetBoolean(11));
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<CommandReceipt> SaveAsync(Guid jobId, ScheduleDefinition proposed, MissedWorkPolicy policy, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor); policy.Validate();
        if (jobId == Guid.Empty || proposed.ScheduleId == Guid.Empty || expectedVersion is not { Length: 8 }) throw new WorkerDockException("ValidationFailed", "schedule");
        if (evaluator.Validate(proposed).Count != 0) throw new WorkerDockException("ValidationFailed", "schedule");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection, transaction, token);
                await using var job = Command(connection, transaction, "SELECT Version,Archived,Enabled,ActivationBoundaryUtc FROM [workerdock].Jobs WITH(UPDLOCK,HOLDLOCK) WHERE JobId=@job");
                Add(job, "@job", SqlDbType.UniqueIdentifier, jobId); await using var jobRow = await job.ExecuteReaderAsync(token);
                if (!await jobRow.ReadAsync(token)) throw new WorkerDockException("JobNotFound");
                var version = jobRow.GetFieldValue<byte[]>(0); var archived = jobRow.GetBoolean(1); var enabled = jobRow.GetBoolean(2);
                var activation = enabled || jobRow.IsDBNull(3) ? clock.GetUtcNow() : Utc(jobRow.GetDateTime(3)); await jobRow.CloseAsync();
                if (archived) throw new WorkerDockException("JobArchived");
                if (!expectedVersion.AsSpan().SequenceEqual(version)) throw new WorkerDockException("StaleVersion");
                await using var existing = Command(connection, transaction, "SELECT ScheduleId,Revision,Paused,PausedAtUtc FROM [workerdock].Schedules WITH(UPDLOCK,HOLDLOCK) WHERE JobId=@job");
                Add(existing, "@job", SqlDbType.UniqueIdentifier, jobId); await using var old = await existing.ExecuteReaderAsync(token);
                var scheduleId = proposed.ScheduleId; long revision = 1; var paused = false; DateTime? pausedAt = null;
                if (await old.ReadAsync(token)) { scheduleId = old.GetGuid(0); revision = checked(old.GetInt64(1) + 1); paused = old.GetBoolean(2); pausedAt = old.IsDBNull(3) ? null : old.GetDateTime(3); } await old.CloseAsync();
                var saved = proposed with { ScheduleId = scheduleId, Revision = revision, ActivatedUtc = activation };
                var fingerprint = rules.GetFingerprint(saved.ZoneId);
                var sql = revision == 1
                    ? "INSERT [workerdock].Schedules(ScheduleId,JobId,Revision,Kind,Cron,ZoneId,OneTimeUtc,ActivatedUtc,Paused,PausedAtUtc,CursorUtc,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs,ZoneRulesFingerprint) VALUES(@schedule,@job,@revision,@kind,@cron,@zone,@once,@activated,0,NULL,@activated,@mode,@tolerance,@count,@age,@fingerprint)"
                    : "UPDATE [workerdock].Schedules SET Revision=@revision,Kind=@kind,Cron=@cron,ZoneId=@zone,OneTimeUtc=@once,ActivatedUtc=@activated,Paused=@paused,PausedAtUtc=@pausedAt,Removed=0,CursorUtc=@activated,CatchUpRecoveryThroughUtc=NULL,CatchUpRecoveryCreated=0,MissedMode=@mode,LatenessToleranceMs=@tolerance,CatchUpCount=@count,CatchUpAgeMs=@age,ZoneRulesFingerprint=@fingerprint WHERE JobId=@job";
                await using (var write = Command(connection, transaction, sql)) { Bind(write, saved, jobId, policy, fingerprint); if (revision > 1) { Add(write, "@paused", SqlDbType.Bit, paused); Add(write, "@pausedAt", SqlDbType.DateTime2, pausedAt ?? (object)DBNull.Value); } await write.ExecuteNonQueryAsync(token); }
                await using (var bump = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=CurrentRevision WHERE JobId=@job AND Version=@version")) { Add(bump, "@job", SqlDbType.UniqueIdentifier, jobId); Add(bump, "@version", SqlDbType.Binary, version, 8); if (await bump.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion"); }
                await OutboxAsync(connection, transaction, scheduleId, revision, JsonSerializer.Serialize(new { scheduleId, scheduleRevision = revision }), token);
                var receipt = new CommandReceipt(Guid.NewGuid(), scheduleId, "Pending"); await CommandReceiptAsync(connection, transaction, receipt, "ScheduleSave", token);
                await AuditAsync(connection, transaction, "ScheduleSaved", scheduleId, revision - 1, revision, actor, token); return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<CommandReceipt> DeleteAsync(Guid jobId, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        JobCommandValidation.ValidateActor(actor);
        if (jobId == Guid.Empty || expectedVersion.Length != 8) throw new WorkerDockException("ValidationFailed", "version");
        try
        {
            return await new BusinessTransaction(options).RunAsync<CommandReceipt>(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection, transaction, token);
                await using var read = Command(connection, transaction, "SELECT s.ScheduleId,s.Revision,j.Version FROM [workerdock].Schedules s WITH(UPDLOCK,HOLDLOCK) JOIN [workerdock].Jobs j WITH(UPDLOCK,HOLDLOCK) ON j.JobId=s.JobId WHERE s.JobId=@job");
                Add(read, "@job", SqlDbType.UniqueIdentifier, jobId); await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) throw new WorkerDockException("ScheduleNotFound");
                var id = row.GetGuid(0); var revision = checked(row.GetInt64(1) + 1); var version = row.GetFieldValue<byte[]>(2); await row.CloseAsync();
                if (!version.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
                await using (var remove = Command(connection, transaction, "UPDATE [workerdock].Schedules SET Removed=1,Revision=@revision WHERE JobId=@job")) { Add(remove, "@job", SqlDbType.UniqueIdentifier, jobId); Add(remove, "@revision", SqlDbType.BigInt, revision); await remove.ExecuteNonQueryAsync(token); }
                await using (var bump = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=CurrentRevision WHERE JobId=@job AND Version=@version")) { Add(bump, "@job", SqlDbType.UniqueIdentifier, jobId); Add(bump, "@version", SqlDbType.Binary, version, 8); if (await bump.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion"); }
                await OutboxAsync(connection, transaction, id, revision, JsonSerializer.Serialize(new { scheduleId = id, scheduleRevision = revision, deleted = true }), token);
                var receipt = new CommandReceipt(Guid.NewGuid(), id, "Pending"); await CommandReceiptAsync(connection, transaction, receipt, "ScheduleDelete", token);
                await AuditAsync(connection, transaction, "ScheduleDeleted", id, revision - 1, revision, actor, token); return receipt;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<int> ReconcileZoneRuleChangesAsync(CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection, transaction, token);
                Guid? lastScheduleId;
                await using (var state = Command(connection, transaction, "SELECT LastScheduleId FROM [workerdock].ScheduleRuleScanState WITH(UPDLOCK,HOLDLOCK) WHERE ScanId=1"))
                { var value = await state.ExecuteScalarAsync(token); lastScheduleId = value is null or DBNull ? null : (Guid)value; }
                List<(Guid ScheduleId, Guid JobId, long Revision, string ZoneId, string Fingerprint, byte[] JobVersion)> rows = [];
                await using (var scan = Command(connection, transaction, "SELECT TOP (100) s.ScheduleId,s.JobId,s.Revision,s.ZoneId,s.ZoneRulesFingerprint,j.Version FROM [workerdock].Schedules s WITH(UPDLOCK,READPAST,ROWLOCK) JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.Removed=0 AND (@last IS NULL OR s.ScheduleId>@last) ORDER BY s.ScheduleId"))
                {
                Add(scan, "@last", SqlDbType.UniqueIdentifier, lastScheduleId ?? (object)DBNull.Value);
                await using (var reader = await scan.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<byte[]>(5)));
                }
                if (rows.Count == 0 && lastScheduleId is not null)
                {
                    await using var reset = Command(connection, transaction, "UPDATE [workerdock].ScheduleRuleScanState SET LastScheduleId=NULL WHERE ScanId=1"); await reset.ExecuteNonQueryAsync(token);
                    await using var scan = Command(connection, transaction, "SELECT TOP (100) s.ScheduleId,s.JobId,s.Revision,s.ZoneId,s.ZoneRulesFingerprint,j.Version FROM [workerdock].Schedules s WITH(UPDLOCK,READPAST,ROWLOCK) JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.Removed=0 ORDER BY s.ScheduleId");
                    await using var reader = await scan.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<byte[]>(5)));
                }
                var nextScanId = rows.Count == 100 ? rows[^1].ScheduleId : (Guid?)null;
                await using (var advance = Command(connection, transaction, "UPDATE [workerdock].ScheduleRuleScanState SET LastScheduleId=@last WHERE ScanId=1"))
                { Add(advance, "@last", SqlDbType.UniqueIdentifier, nextScanId ?? (object)DBNull.Value); await advance.ExecuteNonQueryAsync(token); }

                var changed = 0;
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    var current = rules.GetFingerprint(row.ZoneId);
                    if (string.Equals(current, row.Fingerprint, StringComparison.Ordinal)) continue;
                    var revision = checked(row.Revision + 1);
                    await using (var update = Command(connection, transaction, "UPDATE [workerdock].Schedules SET Revision=@revision,ZoneRulesFingerprint=@fingerprint WHERE ScheduleId=@schedule AND Revision=@oldRevision AND Removed=0"))
                    {
                        Add(update, "@revision", SqlDbType.BigInt, revision); Add(update, "@fingerprint", SqlDbType.Char, current, 64); Add(update, "@schedule", SqlDbType.UniqueIdentifier, row.ScheduleId); Add(update, "@oldRevision", SqlDbType.BigInt, row.Revision);
                        if (await update.ExecuteNonQueryAsync(token) != 1) continue;
                    }
                    await using (var bump = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=CurrentRevision WHERE JobId=@job AND Version=@version"))
                    {
                        Add(bump, "@job", SqlDbType.UniqueIdentifier, row.JobId); Add(bump, "@version", SqlDbType.Binary, row.JobVersion, 8);
                        if (await bump.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
                    }
                    await OutboxAsync(connection, transaction, row.ScheduleId, revision, JsonSerializer.Serialize(new { scheduleId = row.ScheduleId, scheduleRevision = revision }), token);
                    var systemActor = new ActorContext("system:timezone-rules", "System", row.ScheduleId);
                    await AuditAsync(connection, transaction, "ScheduleZoneRulesChanged", row.ScheduleId, row.Revision, revision, systemActor, token);
                    changed++;
                }
                return changed;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static void Bind(DbCommand c, ScheduleDefinition s, Guid job, MissedWorkPolicy p, string fingerprint)
    {
        Add(c,"@schedule",SqlDbType.UniqueIdentifier,s.ScheduleId); Add(c,"@job",SqlDbType.UniqueIdentifier,job); Add(c,"@revision",SqlDbType.BigInt,s.Revision);
        Add(c,"@kind",SqlDbType.NVarChar,s.Kind,16); Add(c,"@cron",SqlDbType.NVarChar,s.Cron??(object)DBNull.Value,120); Add(c,"@zone",SqlDbType.NVarChar,s.ZoneId,100);
        Add(c,"@once",SqlDbType.DateTime2,s.OneTimeUtc?.UtcDateTime??(object)DBNull.Value); Add(c,"@activated",SqlDbType.DateTime2,s.ActivatedUtc.UtcDateTime);
        Add(c,"@mode",SqlDbType.NVarChar,p.Mode,16); Add(c,"@tolerance",SqlDbType.BigInt,checked((long)p.LatenessTolerance.TotalMilliseconds)); Add(c,"@count",SqlDbType.Int,p.CatchUpCount);
        Add(c,"@age",SqlDbType.BigInt,checked((long)p.CatchUpAge.TotalMilliseconds)); Add(c,"@fingerprint",SqlDbType.Char,fingerprint,64);
    }
    private static async ValueTask OutboxAsync(DbConnection c, DbTransaction t, Guid id, long revision, string payload, CancellationToken ct)
    {
        var digest = WorkerDock.Application.Dispatch.ProjectionEnvelope.ScheduleDigest(id,revision);
        await using var cmd = Command(c,t,"INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState) VALUES(@event,N'ScheduleProjection',@payload,SYSUTCDATETIME(),SYSUTCDATETIME(),1,@entity,@revision,@digest,N'Pending')");
        Add(cmd,"@event",SqlDbType.UniqueIdentifier,Guid.NewGuid()); Add(cmd,"@payload",SqlDbType.NVarChar,payload,-1); Add(cmd,"@entity",SqlDbType.UniqueIdentifier,id); Add(cmd,"@revision",SqlDbType.BigInt,revision); Add(cmd,"@digest",SqlDbType.Char,digest,64); await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async ValueTask CommandReceiptAsync(DbConnection c, DbTransaction t, CommandReceipt receipt, string kind, CancellationToken ct)
    { await using var cmd=Command(c,t,"INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@id,@target,@kind,@state,SYSUTCDATETIME())"); Add(cmd,"@id",SqlDbType.UniqueIdentifier,receipt.CommandId); Add(cmd,"@target",SqlDbType.UniqueIdentifier,receipt.TargetId); Add(cmd,"@kind",SqlDbType.NVarChar,kind,40); Add(cmd,"@state",SqlDbType.NVarChar,receipt.State,20); await cmd.ExecuteNonQueryAsync(ct); }
    private static async ValueTask AuditAsync(DbConnection c, DbTransaction t, string kind, Guid target, long before, long after, ActorContext actor, CancellationToken ct)
    { await using var cmd=Command(c,t,"INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(@id,@kind,@subject,@mode,@correlation,SYSUTCDATETIME(),@details)"); Add(cmd,"@id",SqlDbType.UniqueIdentifier,Guid.NewGuid()); Add(cmd,"@kind",SqlDbType.NVarChar,kind,100); Add(cmd,"@subject",SqlDbType.NVarChar,actor.Subject,300); Add(cmd,"@mode",SqlDbType.NVarChar,actor.AuthenticationMode,40); Add(cmd,"@correlation",SqlDbType.UniqueIdentifier,actor.CorrelationId); Add(cmd,"@details",SqlDbType.NVarChar,JsonSerializer.Serialize(new { targetId=target,beforeRevision=before,afterRevision=after,outcome="Applied" }),4000); await cmd.ExecuteNonQueryAsync(ct); }
    private async ValueTask CheckInstanceAsync(DbConnection c, DbTransaction? t, CancellationToken ct) { await using var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText="SELECT InstanceId FROM [workerdock].TenantSettings"; if ((Guid?)await cmd.ExecuteScalarAsync(ct)!=options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch"); }
    private static DbCommand Command(DbConnection c, DbTransaction t, string sql) { var cmd=c.CreateCommand(); cmd.Transaction=t; cmd.CommandText=sql; return cmd; }
    private static void Add(DbCommand c,string n,SqlDbType type,object value,int size=0) { var p=new SqlParameter(n,type){Value=value}; if(size!=0)p.Size=size; c.Parameters.Add(p); }
    private static DateTimeOffset Utc(DateTime value)=>new(DateTime.SpecifyKind(value,DateTimeKind.Utc));
}
