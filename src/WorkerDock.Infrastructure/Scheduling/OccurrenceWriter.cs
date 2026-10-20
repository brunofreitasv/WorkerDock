using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Scheduling;

public sealed class OccurrenceWriter(TenantDatabaseOptions options, IScheduleEvaluator evaluator, WorkerDock.Application.Templates.ITemplateCatalog? templateCatalog = null) : IOccurrenceWriter
{
    private const int EnumerationLimit = 100;

    public async ValueTask<int> MaterializeAsync(Guid scheduleId, long scheduleRevision, DateTimeOffset throughUtc, CancellationToken ct)
    {
        if (scheduleId == Guid.Empty || scheduleRevision < 1 || throughUtc.Offset != TimeSpan.Zero) throw new WorkerDockException("ValidationFailed", "schedule");
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await using (var identity = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings"))
                    if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                await using var read = Command(connection, transaction, "SELECT s.Revision,s.Kind,s.Cron,s.ZoneId,s.OneTimeUtc,s.ActivatedUtc,s.CursorUtc,s.MissedMode,s.LatenessToleranceMs,s.CatchUpCount,s.CatchUpAgeMs,s.Paused,s.Removed,j.JobId,j.CurrentRevision,j.Enabled,j.Archived,r.ProfileId,p.CurrentRevisionId,s.CatchUpRecoveryThroughUtc,s.CatchUpRecoveryCreated,r.TemplateId,r.TemplateVersion,r.DynamicValuesJson,r.ConfigurationJson FROM [workerdock].Schedules s WITH(UPDLOCK,HOLDLOCK) JOIN [workerdock].Jobs j WITH(UPDLOCK,HOLDLOCK) ON j.JobId=s.JobId JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision LEFT JOIN [workerdock].ConnectionProfiles p ON p.ProfileId=r.ProfileId WHERE s.ScheduleId=@schedule");
                Add(read, "@schedule", SqlDbType.UniqueIdentifier, scheduleId); await using var row = await read.ExecuteReaderAsync(token);
                if (!await row.ReadAsync(token)) return 0;
                var currentScheduleRevision = row.GetInt64(0); if (currentScheduleRevision != scheduleRevision || row.GetBoolean(11) || row.GetBoolean(12) || !row.GetBoolean(15) || row.GetBoolean(16)) return 0;
                var jobId = row.GetGuid(13); var jobRevision = row.GetInt64(14); Guid? profileRevision = row.IsDBNull(18) ? null : row.GetGuid(18);
                if (!row.IsDBNull(17) && profileRevision is null) return 0;
                var definition = new ScheduleDefinition(scheduleId, scheduleRevision, row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2), row.GetString(3),
                    row.IsDBNull(4) ? null : Utc(row.GetDateTime(4)), Utc(row.GetDateTime(5)));
                var cursor = Utc(row.GetDateTime(6)); var policy = new MissedWorkPolicy(row.GetString(7), TimeSpan.FromMilliseconds(row.GetInt64(8)), row.GetInt32(9), TimeSpan.FromMilliseconds(row.GetInt64(10)));
                var templateId = row.GetString(21); var templateVersion = row.GetString(22);
                using var dynamicJson = row.IsDBNull(23) ? null : JsonDocument.Parse(row.GetString(23));
                var plan = dynamicJson is null ? WorkerDock.Contracts.Values.DynamicValueJson.EmptyPlan : WorkerDock.Contracts.Values.DynamicValueJson.Read(dynamicJson.RootElement);
                var descriptor = templateCatalog is null ? null : await templateCatalog.GetAsync(templateId, templateVersion, token);
                var capable = descriptor is not null && ContainsDynamicAnnotation(descriptor.ConfigurationSchema);
                DateTimeOffset? recoveryHorizon = row.IsDBNull(19) ? null : Utc(row.GetDateTime(19)); var recoveryCreated = row.GetInt32(20);
                await row.CloseAsync(); policy.Validate();
                var materializationThrough = recoveryHorizon ?? throughUtc;
                if (materializationThrough.UtcDateTime <= cursor)
                {
                    if (recoveryHorizon is not null && cursor >= recoveryHorizon.Value)
                        await AdvanceCursorAsync(connection, transaction, scheduleId, scheduleRevision, cursor.UtcDateTime, null, 0, token);
                    return 0;
                }
                if (evaluator.Validate(definition).Count != 0) throw new WorkerDockException("InvalidSchedule");
                var raw = evaluator.Next(definition, cursor, EnumerationLimit);
                var due = raw.Where(x => x <= materializationThrough).ToArray();
                if (due.Length == 0)
                {
                    await AdvanceCursorAsync(connection, transaction, scheduleId, scheduleRevision, materializationThrough.UtcDateTime, null, 0, token);
                    return 0;
                }
                var bounded = raw.Count == EnumerationLimit && raw[^1] <= materializationThrough;
                var late = due.Any(value => materializationThrough - value > policy.LatenessTolerance);
                var startsCatchUpRecovery = recoveryHorizon is null && policy.Mode == "CatchUp" && bounded && late;
                var isCatchUpRecovery = recoveryHorizon is not null || startsCatchUpRecovery;
                var budget = policy.Mode == "CatchUp"
                    ? new CatchUpRecoveryBudget(policy.CatchUpCount, recoveryHorizon is null ? 0 : recoveryCreated)
                    : null;
                IReadOnlyList<DateTimeOffset> selected = policy.SelectOccurrences(due, definition.ActivatedUtc, materializationThrough, budget?.Remaining);
                DateTime cursorAfter;
                bool exactCount = !bounded && (policy.Mode != "CatchUp" || due.Count(value => materializationThrough - value > policy.LatenessTolerance) <= policy.CatchUpCount);
                if (policy.Mode == "Coalesce" && bounded)
                {
                    selected = selected.Where(value => materializationThrough - value <= policy.LatenessTolerance).ToArray();
                    cursorAfter = due[^1].UtcDateTime; exactCount = false;
                }
                else
                {
                    cursorAfter = bounded ? due[^1].UtcDateTime : materializationThrough.UtcDateTime;
                }
                var created = 0;
                foreach (var scheduledUtc in selected)
                {
                    var executionId = Guid.NewGuid();
                    var idempotency = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scheduleId:N}|{scheduleRevision}|{scheduledUtc.UtcTicks}")));
                    var valueContext = capable ? WorkerDock.Infrastructure.Values.ExecutionValueContextFactory.Scheduled(plan, scheduledUtc) : null;
                    await using var insert = Command(connection, transaction, "INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,ProfileRevisionId,TriggerKind,ScheduleId,ScheduleRevision,ScheduledForUtc,RequestKey,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc,ValueContextJson) VALUES(@id,@job,@jobRevision,@profile,N'Scheduled',@schedule,@scheduleRevision,@scheduled,NULL,@idempotency,N'Queued',N'Known',@scheduled,@correlation,SYSUTCDATETIME(),@context)");
                    Add(insert,"@id",SqlDbType.UniqueIdentifier,executionId); Add(insert,"@job",SqlDbType.UniqueIdentifier,jobId); Add(insert,"@jobRevision",SqlDbType.BigInt,jobRevision); Add(insert,"@profile",SqlDbType.UniqueIdentifier,profileRevision??(object)DBNull.Value); Add(insert,"@schedule",SqlDbType.UniqueIdentifier,scheduleId); Add(insert,"@scheduleRevision",SqlDbType.BigInt,scheduleRevision); Add(insert,"@scheduled",SqlDbType.DateTime2,scheduledUtc.UtcDateTime); Add(insert,"@idempotency",SqlDbType.NVarChar,idempotency,100); Add(insert,"@correlation",SqlDbType.UniqueIdentifier,scheduleId); Add(insert,"@context",SqlDbType.NVarChar,valueContext is null ? DBNull.Value : JsonSerializer.Serialize(valueContext),-1); await insert.ExecuteNonQueryAsync(token);
                    await using var status = Command(connection, transaction, "INSERT [workerdock].AttemptStatusEvents(EventId,ExecutionId,State,OccurredAtUtc) VALUES(NEWID(),@id,N'Queued',SYSUTCDATETIME())"); Add(status,"@id",SqlDbType.UniqueIdentifier,executionId); await status.ExecuteNonQueryAsync(token);
                    created++;
                }
                var missed = due.Count(value => materializationThrough - value > policy.LatenessTolerance);
                var selectedMissed = selected.Count(value => materializationThrough - value > policy.LatenessTolerance);
                var selectedTimely = selected.Count - selectedMissed;
                var skippedOccurrences = Math.Max(0, missed - selectedMissed) + (isCatchUpRecovery ? Math.Max(0, due.Length - missed - selectedTimely) : 0);
                if (late || (isCatchUpRecovery && skippedOccurrences > 0))
                {
                    await using var recovery = Command(connection, transaction, "INSERT [workerdock].ScheduleRecoveryEvents(RecoveryEventId,ScheduleId,ScheduleRevision,FromUtc,ThroughUtc,Policy,CreatedExecutions,ExactMissedCount,SkippedOccurrences) VALUES(NEWID(),@schedule,@revision,@from,@through,@policy,@created,@exact,@skipped)");
                    Add(recovery,"@schedule",SqlDbType.UniqueIdentifier,scheduleId); Add(recovery,"@revision",SqlDbType.BigInt,scheduleRevision); Add(recovery,"@from",SqlDbType.DateTime2,cursor.UtcDateTime); Add(recovery,"@through",SqlDbType.DateTime2,materializationThrough.UtcDateTime); Add(recovery,"@policy",SqlDbType.NVarChar,policy.Mode,16); Add(recovery,"@created",SqlDbType.Int,created); Add(recovery,"@exact",SqlDbType.BigInt,exactCount?missed:(object)DBNull.Value); Add(recovery,"@skipped",SqlDbType.BigInt,skippedOccurrences); await recovery.ExecuteNonQueryAsync(token);
                }
                var keepCatchUpRecovery = isCatchUpRecovery && bounded;
                var nextBudget = keepCatchUpRecovery ? (budget ?? new CatchUpRecoveryBudget(policy.CatchUpCount, 0)).Consume(selected.Count) : null;
                DateTimeOffset? nextRecoveryHorizon = keepCatchUpRecovery ? recoveryHorizon ?? materializationThrough : null;
                await AdvanceCursorAsync(connection, transaction, scheduleId, scheduleRevision, cursorAfter, nextRecoveryHorizon?.UtcDateTime, nextBudget?.Created ?? 0, token);
                await JobAdmission.AdmitAsync(connection, transaction, jobId, token);
                return created;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static async ValueTask AdvanceCursorAsync(DbConnection c, DbTransaction t, Guid id, long revision, DateTime cursor, DateTime? recoveryThrough, int recoveryCreated, CancellationToken ct)
    {
        await using var command=Command(c,t,"UPDATE [workerdock].Schedules SET CursorUtc=@cursor,CatchUpRecoveryThroughUtc=@recoveryThrough,CatchUpRecoveryCreated=@recoveryCreated WHERE ScheduleId=@id AND Revision=@revision AND Paused=0 AND Removed=0"); Add(command,"@cursor",SqlDbType.DateTime2,cursor); Add(command,"@recoveryThrough",SqlDbType.DateTime2,recoveryThrough??(object)DBNull.Value); Add(command,"@recoveryCreated",SqlDbType.Int,recoveryCreated); Add(command,"@id",SqlDbType.UniqueIdentifier,id); Add(command,"@revision",SqlDbType.BigInt,revision); if(await command.ExecuteNonQueryAsync(ct)!=1)throw new WorkerDockException("StaleScheduleRevision");
    }
    private static DateTimeOffset Utc(DateTime value)=>new(DateTime.SpecifyKind(value,DateTimeKind.Utc));
    private static bool ContainsDynamicAnnotation(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.NameEquals("x-workerDock-dynamicValue") && property.Value.ValueKind == JsonValueKind.True || ContainsDynamicAnnotation(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsDynamicAnnotation),
        _ => false
    };
}
