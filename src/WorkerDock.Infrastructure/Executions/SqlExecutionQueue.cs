using System.Data;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Executions;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Executions;

public sealed class SqlExecutionQueue(TenantDatabaseOptions options) : IExecutionQueue
{
    public async ValueTask<IReadOnlyList<Guid>> ReadReadyAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            await using (var identity = new SqlCommand("SELECT InstanceId FROM [workerdock].TenantSettings", connection))
            {
                if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId)
                    throw new WorkerDockException("TenantInstanceMismatch");
            }
            await using var command = new SqlCommand("""
                WITH Candidates AS (
                    SELECT e.ExecutionId,e.JobId,e.State,e.AvailableAtUtc,e.CreatedAtUtc,e.AdmissionSequence,
                        j.ConcurrencyLimit,r.TemplateId,r.TemplateVersion,
                        COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue') AS OverlapMode,
                        COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.BacklogLimit')),100) AS BacklogLimit,
                        COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.Priority')),0) AS Priority,
                        p.EffectiveLimit,
                        (SELECT COUNT(*) FROM [workerdock].Executions active
                            WHERE active.JobId=e.JobId AND active.State IN(N'Running',N'RetryWaiting',N'NeedsReview')) AS ActiveJobSlots,
                        CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Executions active
                            JOIN [workerdock].JobRevisions activeRevision ON activeRevision.JobId=active.JobId AND activeRevision.Revision=active.JobRevision
                            WHERE active.JobId=e.JobId AND active.State IN(N'Running',N'RetryWaiting',N'NeedsReview')
                                AND COALESCE(JSON_VALUE(activeRevision.PolicyJson,'$.Overlap'),N'Queue')<>N'Allow') THEN 1 ELSE 0 END) AS ExclusiveJobSlot,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                            WHERE prior.JobId=e.JobId AND prior.State=N'Queued' AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence) AS EarlierQueued,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                            JOIN [workerdock].JobRevisions priorRevision ON priorRevision.JobId=prior.JobId AND priorRevision.Revision=prior.JobRevision
                            WHERE prior.JobId=e.JobId AND prior.State=N'Queued' AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence
                                AND COALESCE(JSON_VALUE(priorRevision.PolicyJson,'$.Overlap'),N'Queue')=N'Allow') AS EarlierAllow,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                            JOIN [workerdock].JobRevisions priorRevision ON priorRevision.JobId=prior.JobId AND priorRevision.Revision=prior.JobRevision
                            WHERE prior.JobId=e.JobId AND prior.State=N'Queued' AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence
                                AND COALESCE(JSON_VALUE(priorRevision.PolicyJson,'$.Overlap'),N'Queue')=N'Skip') AS EarlierSkip,
                        (SELECT COUNT(*) FROM [workerdock].Executions active
                            JOIN [workerdock].JobRevisions activeRevision ON activeRevision.JobId=active.JobId AND activeRevision.Revision=active.JobRevision
                            WHERE active.State IN(N'Running',N'RetryWaiting',N'NeedsReview') AND activeRevision.TemplateId=r.TemplateId
                                AND (active.ExecutionId<>e.ExecutionId OR e.State<>N'RetryWaiting')) AS ActiveTemplateSlots,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                            WHERE prior.JobId=e.JobId AND prior.State=N'Queued' AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence) AS WaitingBefore
                    FROM [workerdock].Executions e
                    JOIN [workerdock].Jobs j ON j.JobId=e.JobId
                    JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                    JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
                    JOIN [workerdock].TemplatePolicies p ON p.TemplateId=r.TemplateId
                    WHERE e.State IN(N'Queued',N'RetryWaiting') AND e.AvailableAtUtc<=SYSUTCDATETIME()
                        AND e.StopRequested=0 AND j.Enabled=1 AND j.Archived=0 AND cat.Enabled=1
                        AND p.StartGateClosed=0 AND p.EffectiveLimit>0
                )
                SELECT TOP (@limit) candidate.ExecutionId
                FROM Candidates candidate
                WHERE candidate.State=N'RetryWaiting'
                    OR candidate.OverlapMode=N'Skip'
                    OR (candidate.State=N'Queued' AND candidate.WaitingBefore>=candidate.BacklogLimit)
                    OR (candidate.ActiveTemplateSlots<candidate.EffectiveLimit
                        AND ((candidate.State=N'Queued' AND candidate.OverlapMode=N'Queue'
                            AND candidate.ActiveJobSlots=0 AND candidate.EarlierQueued=0)
                        OR (candidate.State=N'Queued' AND candidate.OverlapMode=N'Allow'
                            AND candidate.ActiveJobSlots<candidate.ConcurrencyLimit
                            AND candidate.ActiveJobSlots+candidate.EarlierAllow<candidate.ConcurrencyLimit
                            AND candidate.ExclusiveJobSlot=0
                            AND NOT EXISTS(SELECT 1 FROM [workerdock].Executions prior
                                JOIN [workerdock].JobRevisions priorRevision ON priorRevision.JobId=prior.JobId AND priorRevision.Revision=prior.JobRevision
                                WHERE prior.JobId=candidate.JobId AND prior.State=N'Queued' AND prior.StopRequested=0 AND prior.AdmissionSequence<candidate.AdmissionSequence
                                    AND COALESCE(JSON_VALUE(priorRevision.PolicyJson,'$.Overlap'),N'Queue')=N'Queue')
                            AND NOT (candidate.ActiveJobSlots=0 AND candidate.EarlierAllow=0 AND candidate.EarlierSkip>0)))
                        )
                ORDER BY candidate.Priority DESC,candidate.AvailableAtUtc,candidate.CreatedAtUtc,candidate.ExecutionId
                """, connection);
            command.CommandTimeout = 10;
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
            var ready = new List<Guid>(limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ready.Add(reader.GetGuid(0));
            return ready;
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
}
