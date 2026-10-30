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
                        j.ConcurrencyLimit AS JobConcurrencyLimit,j.PauseOnNeedsReview,
                        COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue') AS OverlapMode,
                        COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.BacklogLimit')),100) AS BacklogLimit,
                        COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.Priority')),0) AS Priority,
                        (SELECT COUNT(*) FROM [workerdock].Attempts a
                            JOIN [workerdock].Executions active ON active.ExecutionId=a.ExecutionId
                            WHERE active.JobId=e.JobId AND a.State=N'Running') AS RunningJobAttempts,
                        CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Attempts a
                            JOIN [workerdock].Executions active ON active.ExecutionId=a.ExecutionId
                            JOIN [workerdock].JobRevisions ar ON ar.JobId=active.JobId AND ar.Revision=active.JobRevision
                            WHERE active.JobId=e.JobId AND a.State=N'Running'
                                AND COALESCE(JSON_VALUE(ar.PolicyJson,'$.Overlap'),N'Queue')<>N'Allow') THEN 1 ELSE 0 END) AS ExclusiveRunning,
                        CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Executions review
                            WHERE review.JobId=e.JobId AND review.State=N'NeedsReview' AND review.AdmissionSequence<e.AdmissionSequence)
                            THEN 1 ELSE 0 END) AS EarlierReview,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                            WHERE prior.JobId=e.JobId AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence
                                AND (prior.State=N'Queued' OR (prior.State=N'RetryWaiting' AND prior.AvailableAtUtc<=SYSUTCDATETIME()))) AS EarlierWaiting,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                            JOIN [workerdock].JobRevisions pr ON pr.JobId=prior.JobId AND pr.Revision=prior.JobRevision
                            WHERE prior.JobId=e.JobId AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence
                                AND (prior.State=N'Queued' OR (prior.State=N'RetryWaiting' AND prior.AvailableAtUtc<=SYSUTCDATETIME()))
                                AND COALESCE(JSON_VALUE(pr.PolicyJson,'$.Overlap'),N'Queue')<>N'Allow') AS EarlierExclusive
                    FROM [workerdock].Executions e
                    JOIN [workerdock].Jobs j ON j.JobId=e.JobId
                    JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                    JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
                    WHERE e.State IN(N'Queued',N'RetryWaiting') AND e.AvailableAtUtc<=SYSUTCDATETIME()
                        AND e.StopRequested=0 AND j.Enabled=1 AND j.Archived=0 AND cat.Enabled=1
                )
                SELECT TOP (@limit) candidate.ExecutionId
                FROM Candidates candidate
                WHERE (candidate.State=N'Queued' AND (
                        (candidate.OverlapMode=N'Skip' AND (candidate.RunningJobAttempts>0 OR candidate.EarlierWaiting>0
                            OR (candidate.PauseOnNeedsReview=1 AND candidate.EarlierReview=1)))
                        OR candidate.EarlierWaiting>=candidate.BacklogLimit))
                    OR (candidate.RunningJobAttempts<candidate.JobConcurrencyLimit AND candidate.ExclusiveRunning=0
                        AND (candidate.OverlapMode=N'Allow' OR candidate.PauseOnNeedsReview=0 OR candidate.EarlierReview=0)
                        AND ((candidate.OverlapMode<>N'Allow' AND candidate.RunningJobAttempts=0 AND candidate.EarlierWaiting=0)
                            OR (candidate.OverlapMode=N'Allow' AND candidate.EarlierExclusive=0
                                AND candidate.RunningJobAttempts+candidate.EarlierWaiting<candidate.JobConcurrencyLimit))
                        AND EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus runtime
                            WHERE runtime.StateId=1 AND runtime.Ready=1 AND runtime.LastHeartbeatUtc>DATEADD(second,-30,SYSUTCDATETIME())
                                AND runtime.HostCeiling>(SELECT COUNT(*) FROM [workerdock].Attempts WHERE State=N'Running')))
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
