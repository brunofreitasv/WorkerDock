using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Dispatch;

public sealed record AdmissionCandidate(Guid ExecutionId, string Mode, int BacklogLimit, bool HoldsPermit = false);
public sealed record AdmissionDecision(IReadOnlyList<Guid> Admit, IReadOnlyList<Guid> Skip, IReadOnlyDictionary<Guid,string> SkipReasons);
public sealed class JobAdmission(TenantDatabaseOptions options)
{
    public const int MaximumCandidates = 10032;
    public const int MaximumChanges = 32;
    public async ValueTask<int> AdmitAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (c, t, token) =>
            {
                await using var identity = Command(c, t, "SELECT InstanceId FROM [workerdock].TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                return await AdmitAsync(c, t, jobId, token);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    internal static async ValueTask<int> AdmitAsync(DbConnection c, DbTransaction t, Guid jobId, CancellationToken ct)
    {
        await using var job = Command(c, t, "SELECT ConcurrencyLimit,Enabled,Archived FROM [workerdock].Jobs WHERE JobId=@id");
        Add(job, "@id", SqlDbType.UniqueIdentifier, jobId);
        await using var jr = await job.ExecuteReaderAsync(ct);
        if (!await jr.ReadAsync(ct)) throw new WorkerDockException("JobNotFound");
        var liveLimit = jr.GetBoolean(1) && !jr.GetBoolean(2) ? jr.GetInt32(0) : 0;
        await jr.CloseAsync();
        await using var holders = Command(c, t, "SELECT COUNT(*),COALESCE(MAX(CASE WHEN COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue')=N'Allow' THEN 0 ELSE 1 END),0) FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision WHERE e.JobId=@job AND e.HoldsJobSlot=1");
        Add(holders, "@job", SqlDbType.UniqueIdentifier, jobId);
        await using var hr = await holders.ExecuteReaderAsync(ct); await hr.ReadAsync(ct);
        var holderCount = hr.GetInt32(0); var exclusiveHeld = hr.GetInt32(1) != 0; await hr.CloseAsync();
        await using var read = Command(c, t, """
            SELECT TOP (10032) e.ExecutionId,COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue'),
                COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.BacklogLimit')),100),
                e.HoldsJobSlot
            FROM [workerdock].Executions e WITH(INDEX(IX_Executions_AdmissionActive)) JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            WHERE e.JobId=@id AND e.State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview') AND e.HoldsJobSlot=0
            ORDER BY e.AdmissionSequence
            """);
        Add(read, "@id", SqlDbType.UniqueIdentifier, jobId);
        await using var reader = await read.ExecuteReaderAsync(ct); var rows = new List<AdmissionCandidate>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
        await reader.CloseAsync();
        var decisions = Decide(rows, liveLimit, holderCount, exclusiveHeld);
        foreach (var id in decisions.Admit)
        {
            await using var hold = Command(c, t, "UPDATE [workerdock].Executions SET HoldsJobSlot=1 WHERE ExecutionId=@id;");
            Add(hold, "@id", SqlDbType.UniqueIdentifier, id); await hold.ExecuteNonQueryAsync(ct);
        }
        foreach (var id in decisions.Skip)
        {
            await using var skip = Command(c, t, """
                UPDATE [workerdock].Executions SET State=N'Skipped',HoldsJobSlot=0 WHERE ExecutionId=@id AND State=N'Queued';
                IF @@ROWCOUNT=1 INSERT [workerdock].AttemptStatusEvents(EventId,ExecutionId,State,OccurredAtUtc) VALUES(NEWID(),@id,@reason,SYSUTCDATETIME());
                UPDATE [workerdock].Outbox SET ProjectionState=N'Retired' WHERE EntityId=@id AND EventType=N'ExecutionDispatch';
                """);
            Add(skip, "@reason", SqlDbType.NVarChar, decisions.SkipReasons[id],40); Add(skip, "@id", SqlDbType.UniqueIdentifier, id); await skip.ExecuteNonQueryAsync(ct);
            await WorkerDock.Infrastructure.Executions.SqlCommandOutcomes.SettleAsync(c, t, id, ct);
        }
        await using var enable = Command(c, t, """
            UPDATE TOP (32) o SET ProjectionState=N'Pending',DispatchedAtUtc=NULL,ReviewAtUtc=NULL,RetryAtUtc=NULL
            FROM [workerdock].Outbox o JOIN [workerdock].Executions e ON e.ExecutionId=o.EntityId JOIN [workerdock].Jobs j ON j.JobId=e.JobId
            JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
            WHERE e.JobId=@job AND j.Enabled=1 AND j.Archived=0 AND e.State IN(N'Queued',N'RetryWaiting') AND e.HoldsJobSlot=1
                AND cat.Enabled=1 AND cat.ConcurrencyLimit>0 AND EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus runtime WHERE runtime.StateId=1 AND runtime.Ready=1 AND runtime.LastHeartbeatUtc>DATEADD(second,-30,SYSUTCDATETIME())) AND o.EventType=N'ExecutionDispatch' AND o.ProjectionState=N'Held'
                AND COALESCE(TRY_CONVERT(int,JSON_VALUE(o.PayloadJson,'$.attemptOrdinal')),1)=1+COALESCE((SELECT MAX(a.AttemptNumber) FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId),0);
            """);
        Add(enable, "@job", SqlDbType.UniqueIdentifier, jobId); await enable.ExecuteNonQueryAsync(ct);
        await BindDeliveriesAsync(c, t, jobId, ct);
        return decisions.Admit.Count;
    }

    // A delivery epoch is immutable once Quartz can acquire it. Rebinding creates a new outbox identity.
    private static async ValueTask BindDeliveriesAsync(DbConnection c, DbTransaction t, Guid jobId, CancellationToken ct)
    {
        await using var read = Command(c, t, """
            SELECT TOP (32) o.EventId,e.ExecutionId,e.JobRevision,COALESCE(TRY_CONVERT(int,JSON_VALUE(o.PayloadJson,'$.attemptOrdinal')),1),
                e.AvailableAtUtc,CONVERT(bigint,0)
            FROM [workerdock].Outbox o JOIN [workerdock].Executions e ON e.ExecutionId=o.EntityId
            JOIN [workerdock].Jobs j ON j.JobId=e.JobId
            JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
            WHERE e.JobId=@job AND j.Enabled=1 AND j.Archived=0 AND e.State IN(N'Queued',N'RetryWaiting') AND e.HoldsJobSlot=1
                AND cat.Enabled=1 AND cat.ConcurrencyLimit>0 AND EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus runtime WHERE runtime.StateId=1 AND runtime.Ready=1 AND runtime.LastHeartbeatUtc>DATEADD(second,-30,SYSUTCDATETIME())) AND o.EventType=N'ExecutionDispatch' AND o.ProjectionState<>N'Retired'
                AND COALESCE(TRY_CONVERT(int,JSON_VALUE(o.PayloadJson,'$.attemptOrdinal')),1)=1+COALESCE((SELECT MAX(a.AttemptNumber) FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId),0)
                AND TRY_CONVERT(bigint,JSON_VALUE(o.PayloadJson,'$.capacityRevision'))<>0
            """);
        Add(read, "@job", SqlDbType.UniqueIdentifier, jobId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        var rows = new List<(Guid Old, Guid Execution, long Revision, int Ordinal, DateTime Due, long Epoch)>();
        while (await reader.ReadAsync(ct)) rows.Add((reader.GetGuid(0),reader.GetGuid(1),reader.GetInt64(2),reader.GetInt32(3),reader.GetDateTime(4),reader.GetInt64(5)));
        await reader.CloseAsync();
        foreach (var row in rows)
        {
            var delivery = WorkerDock.Application.Dispatch.DispatchIntent.CapacityDeliveryId(row.Execution,row.Ordinal,row.Epoch);
            await using var bind = Command(c, t, """
                IF EXISTS(SELECT 1 FROM [workerdock].Outbox WHERE EventId=@id AND
                    (EntityId<>@execution OR EntityRevision<>@revision OR PayloadDigest<>@digest OR EventType<>N'ExecutionDispatch'))
                    THROW 51013,'DispatchIntegrityError',1;
                UPDATE [workerdock].Outbox SET ProjectionState=N'Retired' WHERE EventId=@old;
                IF NOT EXISTS(SELECT 1 FROM [workerdock].Outbox WHERE EventId=@id)
                INSERT [workerdock].Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState)
                VALUES(@id,N'ExecutionDispatch',@payload,SYSUTCDATETIME(),@due,1,@execution,@revision,@digest,N'Pending');
                """);
            Add(bind, "@old", SqlDbType.UniqueIdentifier, row.Old); Add(bind, "@id", SqlDbType.UniqueIdentifier, delivery);
            Add(bind, "@execution", SqlDbType.UniqueIdentifier, row.Execution); Add(bind, "@revision", SqlDbType.BigInt, row.Revision); Add(bind, "@due", SqlDbType.DateTime2, row.Due);
            Add(bind, "@digest", SqlDbType.Char, WorkerDock.Application.Dispatch.ProjectionEnvelope.ExecutionDigest(row.Execution,row.Revision,row.Ordinal,row.Epoch),64);
            Add(bind, "@payload", SqlDbType.NVarChar, JsonSerializer.Serialize(new { executionId=row.Execution,jobRevision=row.Revision,attemptOrdinal=row.Ordinal,capacityRevision=row.Epoch }),-1);
            await bind.ExecuteNonQueryAsync(ct);
        }
    }
    public static AdmissionDecision Decide(IReadOnlyList<AdmissionCandidate> ordered, int liveLimit, int heldCount = 0, bool exclusiveHeld = false)
    {
        if (liveLimit < 0) throw new ArgumentOutOfRangeException(nameof(liveLimit));
        var holders = ordered.Where(x => x.HoldsPermit).ToArray();
        var count = heldCount + holders.Length; var exclusive = exclusiveHeld || holders.Any(x => x.Mode != "Allow");
        var admit = new List<Guid>(); var skip = new List<Guid>(); var reasons = new Dictionary<Guid,string>();
        var waiting = 0; var blocked = false; var outstanding = count > 0;
        foreach (var work in ordered.Where(x => !x.HoldsPermit).Take(MaximumCandidates))
        {
            if (work.Mode == "Skip" && outstanding) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedOverlap"; continue; }
            var canAdmit = !blocked && !exclusive && count < liveLimit && (work.Mode == "Allow" || count == 0);
            if (canAdmit)
            {
                admit.Add(work.ExecutionId); count++; exclusive = work.Mode != "Allow"; outstanding = true;
            }
            else if (waiting >= work.BacklogLimit) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedBacklog"; }
            else { waiting++; blocked = true; outstanding = true; }
        }
        var committedAdmit = admit.Take(MaximumChanges).ToArray();
        var committedSkip = skip.Take(MaximumChanges - committedAdmit.Length).ToArray();
        return new(committedAdmit, committedSkip, committedSkip.ToDictionary(id => id, id => reasons[id]));
    }
}
