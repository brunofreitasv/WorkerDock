using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Dispatch;

public sealed record AdmissionCandidate(Guid ExecutionId, string Mode, int BacklogLimit, bool HoldsJobSlot = false, string State = "Queued", bool IsReady = true);
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
        await using var job = Command(c, t, "SELECT ConcurrencyLimit,Enabled,Archived,PauseOnNeedsReview FROM [workerdock].Jobs WHERE JobId=@id");
        Add(job, "@id", SqlDbType.UniqueIdentifier, jobId);
        await using var jr = await job.ExecuteReaderAsync(ct);
        if (!await jr.ReadAsync(ct)) throw new WorkerDockException("JobNotFound");
        var liveLimit = jr.GetBoolean(1) && !jr.GetBoolean(2) ? jr.GetInt32(0) : 0;
        var pauseOnNeedsReview = jr.GetBoolean(3);
        await jr.CloseAsync();
        await using var read = Command(c, t, """
            SELECT TOP (10032) e.ExecutionId,COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue'),
                COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.BacklogLimit')),100),
                CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId AND a.State=N'Running') THEN 1 ELSE 0 END),e.State,
                CONVERT(bit,CASE WHEN e.AvailableAtUtc<=SYSUTCDATETIME() THEN 1 ELSE 0 END)
            FROM [workerdock].Executions e WITH(INDEX(IX_Executions_AdmissionActive)) JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            WHERE e.JobId=@id AND e.State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview') AND (e.State NOT IN(N'Queued',N'RetryWaiting') OR e.StopRequested=0)
            ORDER BY e.AdmissionSequence
            """);
        Add(read, "@id", SqlDbType.UniqueIdentifier, jobId);
        await using var reader = await read.ExecuteReaderAsync(ct); var rows = new List<AdmissionCandidate>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetString(4), reader.GetBoolean(5)));
        await reader.CloseAsync();
        var decisions = Decide(rows, liveLimit, pauseOnNeedsReview: pauseOnNeedsReview);
        await using var release = Command(c, t, "UPDATE [workerdock].Executions SET HoldsJobSlot=0 WHERE JobId=@job AND State<>N'Running'");
        Add(release, "@job", SqlDbType.UniqueIdentifier, jobId); await release.ExecuteNonQueryAsync(ct);
        foreach (var id in decisions.Admit)
        {
            await using var hold = Command(c, t, "UPDATE [workerdock].Executions SET HoldsJobSlot=1 WHERE ExecutionId=@id;");
            Add(hold, "@id", SqlDbType.UniqueIdentifier, id); await hold.ExecuteNonQueryAsync(ct);
        }
        foreach (var id in decisions.Skip)
        {
            await using var skip = Command(c, t, """
                UPDATE [workerdock].Executions SET State=N'Skipped',HoldsJobSlot=0 WHERE ExecutionId=@id AND State=N'Queued';
                IF @@ROWCOUNT=1 INSERT [workerdock].OperationalEvents(EventId,EventKind,ExecutionId,Source,Sequence,AtUtc,Code,SafeFieldsJson)
                    VALUES(NEWID(),N'Execution',@id,N'Lifecycle',0,SYSUTCDATETIME(),@reason,N'{}');
                """);
            Add(skip, "@reason", SqlDbType.NVarChar, decisions.SkipReasons[id],40); Add(skip, "@id", SqlDbType.UniqueIdentifier, id); await skip.ExecuteNonQueryAsync(ct);
            await WorkerDock.Infrastructure.Executions.SqlCommandOutcomes.SettleAsync(c, t, id, ct);
        }
        return decisions.Admit.Count;
    }
    public static AdmissionDecision Decide(IReadOnlyList<AdmissionCandidate> ordered, int liveLimit, int heldCount = 0, bool exclusiveHeld = false, bool pauseOnNeedsReview = false)
    {
        if (liveLimit < 0) throw new ArgumentOutOfRangeException(nameof(liveLimit));
        var holders = ordered.Where(x => x.HoldsJobSlot).ToArray();
        var count = heldCount + holders.Length; var exclusive = exclusiveHeld || holders.Any(x => x.Mode != "Allow");
        var admit = new List<Guid>(); var skip = new List<Guid>(); var reasons = new Dictionary<Guid,string>();
        var waiting = 0; var blocked = false; var outstanding = count > 0; var reviewPending = false;
        foreach (var work in ordered.Where(x => !x.HoldsJobSlot).Take(MaximumCandidates))
        {
            if (work.State == "NeedsReview") { reviewPending |= pauseOnNeedsReview; continue; }
            if (work.State == "Running" || (work.State == "RetryWaiting" && !work.IsReady)) continue;
            if (work.State == "Queued" && work.Mode == "Skip" && reviewPending) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedReviewHold"; continue; }
            if (work.State == "Queued" && work.Mode == "Skip" && outstanding) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedOverlap"; continue; }
            var canAdmit = !(reviewPending && work.Mode != "Allow") && !blocked && !exclusive && count < liveLimit && (work.Mode == "Allow" || count == 0);
            if (canAdmit)
            {
                admit.Add(work.ExecutionId); count++; exclusive = work.Mode != "Allow"; outstanding = true;
            }
            else if (work.State == "Queued" && waiting >= work.BacklogLimit) { skip.Add(work.ExecutionId); reasons[work.ExecutionId]="SkippedBacklog"; }
            else { waiting++; blocked = true; outstanding = true; }
        }
        var committedAdmit = admit.Take(MaximumChanges).ToArray();
        var committedSkip = skip.Take(MaximumChanges - committedAdmit.Length).ToArray();
        return new(committedAdmit, committedSkip, committedSkip.ToDictionary(id => id, id => reasons[id]));
    }
}
