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
                SELECT TOP (@limit) e.ExecutionId
                FROM [workerdock].Executions e
                JOIN [workerdock].Jobs j ON j.JobId=e.JobId
                JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
                JOIN [workerdock].TemplatePolicies p ON p.TemplateId=r.TemplateId
                WHERE e.State IN(N'Queued',N'RetryWaiting') AND e.AvailableAtUtc<=SYSUTCDATETIME()
                    AND e.StopRequested=0 AND j.Enabled=1 AND j.Archived=0 AND cat.Enabled=1
                    AND p.StartGateClosed=0 AND p.EffectiveLimit>0
                    AND (SELECT COUNT(*) FROM [workerdock].Executions active
                        JOIN [workerdock].JobRevisions activeRevision ON activeRevision.JobId=active.JobId AND activeRevision.Revision=active.JobRevision
                        WHERE active.State IN(N'Running',N'RetryWaiting',N'NeedsReview') AND activeRevision.TemplateId=r.TemplateId)<p.EffectiveLimit
                ORDER BY COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.Priority')),0) DESC,
                    e.AvailableAtUtc,e.CreatedAtUtc,e.ExecutionId
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
