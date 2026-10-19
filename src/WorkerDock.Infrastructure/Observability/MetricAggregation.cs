using System.Data;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Observability;

public static class MetricTagProjection
{
    private static readonly HashSet<string> Templates = new(StringComparer.Ordinal) { "workerdock.sql-server", "workerdock.http" };
    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal) { "Succeeded", "Failed", "Stopped", "TimedOut", "Skipped" };
    private static readonly HashSet<string> Components = new(StringComparer.Ordinal) { "worker", "scheduler", "database", "executor", "application" };

    public static IReadOnlyDictionary<string, string> Create(string? templateId, string? outcome, string? component) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["template_id"] = templateId is not null && Templates.Contains(templateId) ? templateId : "other",
            ["outcome"] = outcome is not null && Outcomes.Contains(outcome) ? outcome : "other",
            ["component"] = component is not null && Components.Contains(component) ? component : "other"
        };
}

public static class MetricAggregation
{
    private const int MaximumBatchSize = 5000;

    public static async ValueTask<int> AggregateAsync(TenantDatabaseOptions options, DateTimeOffset throughUtc, int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > MaximumBatchSize) throw new WorkerDockException("ValidationFailed");
        await using var connection = new SqlConnection(options.ConnectionString);
        try { await connection.OpenAsync(ct); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            DECLARE @Batch TABLE(ExecutionId uniqueidentifier PRIMARY KEY, WindowStartUtc datetime2(7), TemplateId nvarchar(100), Outcome nvarchar(30), Retries int, Timeouts int, DurationMs bigint, TriggerDelayMs bigint, QueueDelayMs bigint);
            INSERT @Batch(ExecutionId,WindowStartUtc,TemplateId,Outcome,Retries,Timeouts,DurationMs,TriggerDelayMs,QueueDelayMs)
            SELECT TOP(@batchSize) e.ExecutionId,
                   DATEADD(hour,DATEDIFF(hour,CONVERT(datetime2(7),'20000101'),e.CreatedAtUtc),CONVERT(datetime2(7),'20000101')),
                   r.TemplateId,e.State,CASE WHEN COUNT(a.AttemptId)>0 THEN COUNT(a.AttemptId)-1 ELSE 0 END,
                   CASE WHEN e.State=N'TimedOut' THEN 1 ELSE 0 END,
                   COALESCE(SUM(CASE WHEN a.StartedAtUtc IS NOT NULL AND a.EndedAtUtc IS NOT NULL THEN DATEDIFF_BIG(millisecond,a.StartedAtUtc,a.EndedAtUtc) ELSE 0 END),0),
                   COALESCE(CASE WHEN e.ScheduledForUtc IS NOT NULL THEN DATEDIFF_BIG(millisecond,e.ScheduledForUtc,MIN(a.StartedAtUtc)) ELSE 0 END,0),
                   COALESCE(CASE WHEN MIN(a.StartedAtUtc) IS NOT NULL THEN DATEDIFF_BIG(millisecond,e.AvailableAtUtc,MIN(a.StartedAtUtc)) ELSE 0 END,0)
              FROM [workerdock].Executions e WITH(UPDLOCK,READPAST,ROWLOCK)
              JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
              LEFT JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
             WHERE e.AggregatedAtUtc IS NULL AND e.CreatedAtUtc<=@throughUtc AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped')
             GROUP BY e.ExecutionId,e.CreatedAtUtc,r.TemplateId,e.State,e.ScheduledForUtc,e.AvailableAtUtc
             ORDER BY e.CreatedAtUtc,e.ExecutionId;
            IF NOT EXISTS(SELECT 1 FROM @Batch) BEGIN SELECT 0 AS Processed; RETURN; END;
            UPDATE e SET AggregatedAtUtc=SYSUTCDATETIME() FROM [workerdock].Executions e JOIN @Batch b ON b.ExecutionId=e.ExecutionId;
            MERGE [workerdock].ExecutionMetricAggregates WITH(HOLDLOCK) AS target
            USING (SELECT WindowStartUtc,TemplateId,Outcome,COUNT_BIG(*) ExecutionCount,SUM(CONVERT(bigint,Retries)) RetryCount,SUM(CONVERT(bigint,Timeouts)) TimeoutCount,
                          SUM(DurationMs) DurationMsTotal,SUM(TriggerDelayMs) TriggerDelayMsTotal,SUM(QueueDelayMs) QueueDelayMsTotal
                     FROM @Batch GROUP BY WindowStartUtc,TemplateId,Outcome) AS source
               ON target.WindowStartUtc=source.WindowStartUtc AND target.TemplateId=source.TemplateId AND target.Outcome=source.Outcome
            WHEN MATCHED THEN UPDATE SET ExecutionCount=target.ExecutionCount+source.ExecutionCount,RetryCount=target.RetryCount+source.RetryCount,
                 TimeoutCount=target.TimeoutCount+source.TimeoutCount,DurationMsTotal=target.DurationMsTotal+source.DurationMsTotal,
                 TriggerDelayMsTotal=target.TriggerDelayMsTotal+source.TriggerDelayMsTotal,QueueDelayMsTotal=target.QueueDelayMsTotal+source.QueueDelayMsTotal,UpdatedAtUtc=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT(WindowStartUtc,TemplateId,Outcome,ExecutionCount,RetryCount,TimeoutCount,DurationMsTotal,TriggerDelayMsTotal,QueueDelayMsTotal,UpdatedAtUtc)
                 VALUES(source.WindowStartUtc,source.TemplateId,source.Outcome,source.ExecutionCount,source.RetryCount,source.TimeoutCount,source.DurationMsTotal,source.TriggerDelayMsTotal,source.QueueDelayMsTotal,SYSUTCDATETIME());
            SELECT COUNT(*) AS Processed FROM @Batch;
            SELECT TemplateId,Outcome,COUNT_BIG(*) ExecutionCount,SUM(CONVERT(bigint,Retries)) RetryCount,SUM(CONVERT(bigint,Timeouts)) TimeoutCount,
                   SUM(DurationMs) DurationMsTotal,SUM(TriggerDelayMs) TriggerDelayMsTotal,SUM(QueueDelayMs) QueueDelayMsTotal
              FROM @Batch GROUP BY TemplateId,Outcome;
            """;
        command.Parameters.Add("@batchSize", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@throughUtc", SqlDbType.DateTime2).Value = throughUtc.UtcDateTime;
        var aggregates = new List<(string Template,string Outcome,long Count,long Retries,long Timeouts,long Duration,long Trigger,long Queue)>();
        int count;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct); count = reader.GetInt32(0);
            if (await reader.NextResultAsync(ct))
                while (await reader.ReadAsync(ct)) aggregates.Add((reader.GetString(0),reader.GetString(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6),reader.GetInt64(7)));
        }
        await transaction.CommitAsync(ct);
        foreach (var aggregate in aggregates)
            TelemetryRegistration.RecordAggregate(aggregate.Template,aggregate.Outcome,aggregate.Count,aggregate.Retries,aggregate.Timeouts,aggregate.Duration,aggregate.Trigger,aggregate.Queue);
        return count;
    }
}
