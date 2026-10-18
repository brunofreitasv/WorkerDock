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
    public async ValueTask<int> AdmitAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (c, t, token) =>
            {
                await using var identity = Command(c, t, "SELECT InstanceId FROM dbo.TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                return await AdmitAsync(c, t, jobId, token);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    internal static async ValueTask<int> AdmitAsync(DbConnection c, DbTransaction t, Guid jobId, CancellationToken ct)
    {
        await using var job = Command(c, t, "SELECT ConcurrencyLimit,Enabled,Archived FROM dbo.Jobs WHERE JobId=@id");
        Add(job, "@id", SqlDbType.UniqueIdentifier, jobId);
        await using var jr = await job.ExecuteReaderAsync(ct);
        if (!await jr.ReadAsync(ct)) throw new WorkerDockException("JobNotFound");
        var liveLimit = jr.GetBoolean(1) && !jr.GetBoolean(2) ? jr.GetInt32(0) : 0;
        await jr.CloseAsync();
        await using var read = Command(c, t, """
            SELECT e.ExecutionId,COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue'),
                COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.BacklogLimit')),100),
                CONVERT(bit,CASE WHEN p.ExecutionId IS NULL THEN 0 ELSE 1 END)
            FROM dbo.Executions e JOIN dbo.JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            LEFT JOIN dbo.JobPermits p ON p.ExecutionId=e.ExecutionId
            WHERE e.JobId=@id AND e.State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview')
            ORDER BY e.AdmissionSequence
            """);
        Add(read, "@id", SqlDbType.UniqueIdentifier, jobId);
        await using var reader = await read.ExecuteReaderAsync(ct); var rows = new List<AdmissionCandidate>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3)));
        await reader.CloseAsync();
        var decisions = Decide(rows, liveLimit);
        foreach (var id in decisions.Admit)
        {
            await using var permit = Command(c, t, "INSERT dbo.JobPermits(PermitId,JobId,ExecutionId,Mode,AcquiredAtUtc) VALUES(NEWID(),@job,@id,@mode,SYSUTCDATETIME());");
            Add(permit, "@job", SqlDbType.UniqueIdentifier, jobId); Add(permit, "@id", SqlDbType.UniqueIdentifier, id);
            Add(permit, "@mode", SqlDbType.NVarChar, rows.Single(x => x.ExecutionId == id).Mode == "Allow" ? "Shared" : "Exclusive", 20); await permit.ExecuteNonQueryAsync(ct);
        }
        foreach (var id in decisions.Skip)
        {
            await using var skip = Command(c, t, """
                UPDATE dbo.Executions SET State=N'Skipped' WHERE ExecutionId=@id AND State=N'Queued';
                IF @@ROWCOUNT=1 INSERT dbo.AttemptStatusEvents(EventId,ExecutionId,State,OccurredAtUtc) VALUES(NEWID(),@id,@reason,SYSUTCDATETIME());
                UPDATE dbo.Outbox SET ProjectionState=N'Retired' WHERE EntityId=@id AND EventType=N'ExecutionDispatch';
                """);
            Add(skip, "@reason", SqlDbType.NVarChar, decisions.SkipReasons[id],40); Add(skip, "@id", SqlDbType.UniqueIdentifier, id); await skip.ExecuteNonQueryAsync(ct);
        }
        await using var enable = Command(c, t, """
            UPDATE o SET ProjectionState=N'Pending',DispatchedAtUtc=NULL,ReviewAtUtc=NULL,RetryAtUtc=NULL
            FROM dbo.Outbox o JOIN dbo.Executions e ON e.ExecutionId=o.EntityId JOIN dbo.Jobs j ON j.JobId=e.JobId
            JOIN dbo.JobPermits permit ON permit.ExecutionId=e.ExecutionId
            JOIN dbo.JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            JOIN dbo.TemplatePolicies p ON p.TemplateId=r.TemplateId
            WHERE e.JobId=@job AND j.Enabled=1 AND j.Archived=0 AND e.State IN(N'Queued',N'RetryWaiting')
                AND p.StartGateClosed=0 AND p.EffectiveLimit>0 AND EXISTS(SELECT 1 FROM dbo.Workers w WHERE w.State=N'Ready' AND w.AcknowledgedRevision=p.EffectiveRevision
            AND w.CatalogDigest=(SELECT CatalogDigest FROM dbo.PolicyRevisions WHERE PolicyKind=N'Capacity')
            AND w.LastHeartbeatUtc>DATEADD(second,-60,SYSUTCDATETIME())
            AND w.LiveLimit>(SELECT COUNT(*) FROM dbo.Attempts a WHERE a.WorkerBootId=w.BootId AND a.State=N'Running')) AND o.EventType=N'ExecutionDispatch' AND o.ProjectionState=N'Held'
                AND COALESCE(TRY_CONVERT(int,JSON_VALUE(o.PayloadJson,'$.attemptOrdinal')),1)=1+COALESCE((SELECT MAX(a.AttemptNumber) FROM dbo.Attempts a WHERE a.ExecutionId=e.ExecutionId),0);
            """);
        Add(enable, "@job", SqlDbType.UniqueIdentifier, jobId); await enable.ExecuteNonQueryAsync(ct);
        await BindDeliveriesAsync(c, t, jobId, ct);
        return decisions.Admit.Count;
    }

    // A delivery epoch is immutable once Quartz can acquire it. Rebinding creates a new outbox identity.
    private static async ValueTask BindDeliveriesAsync(DbConnection c, DbTransaction t, Guid jobId, CancellationToken ct)
    {
        await using var read = Command(c, t, """
            SELECT o.EventId,e.ExecutionId,e.JobRevision,COALESCE(TRY_CONVERT(int,JSON_VALUE(o.PayloadJson,'$.attemptOrdinal')),1),
                e.AvailableAtUtc,p.EffectiveRevision
            FROM dbo.Outbox o JOIN dbo.Executions e ON e.ExecutionId=o.EntityId
            JOIN dbo.Jobs j ON j.JobId=e.JobId JOIN dbo.JobPermits permit ON permit.ExecutionId=e.ExecutionId
            JOIN dbo.JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            JOIN dbo.TemplatePolicies p ON p.TemplateId=r.TemplateId
            WHERE e.JobId=@job AND j.Enabled=1 AND j.Archived=0 AND e.State IN(N'Queued',N'RetryWaiting')
                AND p.StartGateClosed=0 AND p.EffectiveLimit>0 AND EXISTS(SELECT 1 FROM dbo.Workers w WHERE w.State=N'Ready' AND w.AcknowledgedRevision=p.EffectiveRevision
            AND w.CatalogDigest=(SELECT CatalogDigest FROM dbo.PolicyRevisions WHERE PolicyKind=N'Capacity')
            AND w.LastHeartbeatUtc>DATEADD(second,-60,SYSUTCDATETIME())
            AND w.LiveLimit>(SELECT COUNT(*) FROM dbo.Attempts a WHERE a.WorkerBootId=w.BootId AND a.State=N'Running')) AND o.EventType=N'ExecutionDispatch' AND o.ProjectionState<>N'Retired'
                AND COALESCE(TRY_CONVERT(int,JSON_VALUE(o.PayloadJson,'$.attemptOrdinal')),1)=1+COALESCE((SELECT MAX(a.AttemptNumber) FROM dbo.Attempts a WHERE a.ExecutionId=e.ExecutionId),0)
                AND (TRY_CONVERT(bigint,JSON_VALUE(o.PayloadJson,'$.capacityRevision')) IS NULL
                    OR TRY_CONVERT(bigint,JSON_VALUE(o.PayloadJson,'$.capacityRevision'))<>p.EffectiveRevision)
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
                IF EXISTS(SELECT 1 FROM dbo.Outbox WHERE EventId=@id AND
                    (EntityId<>@execution OR EntityRevision<>@revision OR PayloadDigest<>@digest OR EventType<>N'ExecutionDispatch'))
                    THROW 51013,'DispatchIntegrityError',1;
                UPDATE dbo.Outbox SET ProjectionState=N'Retired' WHERE EventId=@old;
                IF NOT EXISTS(SELECT 1 FROM dbo.Outbox WHERE EventId=@id)
                INSERT dbo.Outbox(EventId,EventType,PayloadJson,CreatedAtUtc,AvailableAtUtc,SchemaVersion,EntityId,EntityRevision,PayloadDigest,ProjectionState)
                VALUES(@id,N'ExecutionDispatch',@payload,SYSUTCDATETIME(),@due,1,@execution,@revision,@digest,N'Pending');
                """);
            Add(bind, "@old", SqlDbType.UniqueIdentifier, row.Old); Add(bind, "@id", SqlDbType.UniqueIdentifier, delivery);
            Add(bind, "@execution", SqlDbType.UniqueIdentifier, row.Execution); Add(bind, "@revision", SqlDbType.BigInt, row.Revision); Add(bind, "@due", SqlDbType.DateTime2, row.Due);
            Add(bind, "@digest", SqlDbType.Char, WorkerDock.Application.Dispatch.ProjectionEnvelope.ExecutionDigest(row.Execution,row.Revision,row.Ordinal,row.Epoch),64);
            Add(bind, "@payload", SqlDbType.NVarChar, JsonSerializer.Serialize(new { executionId=row.Execution,jobRevision=row.Revision,attemptOrdinal=row.Ordinal,capacityRevision=row.Epoch }),-1);
            await bind.ExecuteNonQueryAsync(ct);
        }
    }
    public static AdmissionDecision Decide(IReadOnlyList<AdmissionCandidate> ordered, int liveLimit)
    {
        if (liveLimit < 0) throw new ArgumentOutOfRangeException(nameof(liveLimit));
        var holders = ordered.Where(x => x.HoldsPermit).ToArray();
        var count = holders.Length; var exclusive = holders.Any(x => x.Mode != "Allow");
        var admit = new List<Guid>(); var skip = new List<Guid>(); var reasons = new Dictionary<Guid,string>();
        var waiting = 0; var blocked = false; var outstanding = count > 0;
        foreach (var work in ordered)
        {
            if (work.HoldsPermit) { outstanding = true; continue; }
            if (work.Mode == "Skip" && outstanding) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedOverlap"; continue; }
            var canAdmit = !blocked && !exclusive && count < liveLimit && (work.Mode == "Allow" || count == 0);
            if (canAdmit)
            {
                admit.Add(work.ExecutionId); count++; exclusive = work.Mode != "Allow"; outstanding = true;
            }
            else if (waiting >= work.BacklogLimit) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedBacklog"; }
            else { waiting++; blocked = true; outstanding = true; }
        }
        return new(admit, skip, reasons);
    }
}
