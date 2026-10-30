using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkerDock.Contracts.Templates;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Infrastructure.Runtime;

namespace WorkerDock.Infrastructure.Monitoring;

public sealed class SqlMonitoringQueries(TenantDatabaseOptions options, TimeProvider clock) : IMonitoringQueries
{
    private static readonly string[] ActiveStates = ["Queued", "Running", "RetryWaiting", "NeedsReview"];
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(90);
    private static readonly TimeSpan MaxAuditRange = TimeSpan.FromDays(365);

    public async ValueTask<DashboardDto> DashboardAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @now datetime2(7)=SYSUTCDATETIME();
            SELECT @now, (SELECT InstanceId FROM [workerdock].TenantSettings),
                (SELECT COUNT(*) FROM [workerdock].Executions WHERE State=N'Queued'),
                (SELECT COUNT(*) FROM [workerdock].Executions WHERE State=N'Running'),
                (SELECT COUNT(*) FROM [workerdock].Executions WHERE State=N'NeedsReview'),
                (SELECT COUNT(*) FROM [workerdock].Executions WHERE State=N'Succeeded' AND CreatedAtUtc>=DATEADD(hour,-24,@now)),
                (SELECT COUNT(*) FROM [workerdock].Executions WHERE State IN(N'Failed',N'TimedOut') AND CreatedAtUtc>=DATEADD(hour,-24,@now)),
                (SELECT COUNT(*) FROM [workerdock].Executions e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId AND a.AttemptNumber=(SELECT MAX(a2.AttemptNumber) FROM [workerdock].Attempts a2 WHERE a2.ExecutionId=e.ExecutionId) WHERE e.State=N'Running' AND a.StartedAtUtc<DATEADD(minute,-5,@now)),
                (SELECT COUNT(*) FROM [workerdock].Schedules s JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.Removed=0 AND s.Paused=0 AND j.Enabled=1 AND s.Kind=N'Once' AND s.OneTimeUtc BETWEEN @now AND DATEADD(day,7,@now)),
                COALESCE((SELECT Ready FROM [workerdock].RuntimeStatus WHERE StateId=1),CONVERT(bit,0)),
                COALESCE((SELECT HostCeiling FROM [workerdock].RuntimeStatus WHERE StateId=1),1),
                (SELECT LastHeartbeatUtc FROM [workerdock].RuntimeStatus WHERE StateId=1);
            SELECT TOP(10) j.JobId,r.Name,r.TemplateId,s.OneTimeUtc
              FROM [workerdock].Schedules s JOIN [workerdock].Jobs j ON j.JobId=s.JobId
              JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision
             WHERE s.Removed=0 AND s.Paused=0 AND j.Enabled=1 AND s.Kind=N'Once' AND s.OneTimeUtc>SYSUTCDATETIME()
             ORDER BY s.OneTimeUtc,s.ScheduleId;
            WITH recent AS (
                SELECT TOP(10) e.ExecutionId,e.JobId,r.Name,r.TemplateId,e.State,e.CreatedAtUtc,e.CorrelationId
                  FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                 ORDER BY e.CreatedAtUtc DESC,e.ExecutionId DESC
            ), attemptSummary AS (
                SELECT a.ExecutionId,MIN(a.StartedAtUtc) AS StartedAtUtc,
                       MAX(CASE WHEN a.AttemptNumber=lastAttempt.AttemptNumber THEN a.EndedAtUtc END) AS TerminalEndedAtUtc
                  FROM recent e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                  JOIN (SELECT a2.ExecutionId,MAX(a2.AttemptNumber) AS AttemptNumber FROM [workerdock].Attempts a2 JOIN recent s2 ON s2.ExecutionId=a2.ExecutionId GROUP BY a2.ExecutionId) lastAttempt
                    ON lastAttempt.ExecutionId=a.ExecutionId
                 GROUP BY a.ExecutionId
            )
            SELECT e.ExecutionId,e.JobId,e.Name,e.TemplateId,e.State,e.CreatedAtUtc,e.CorrelationId,
                   a.StartedAtUtc,CASE WHEN e.State IN(N'Succeeded',N'Failed',N'TimedOut',N'Stopped',N'Skipped') AND a.StartedAtUtc IS NOT NULL AND a.TerminalEndedAtUtc IS NOT NULL
                       THEN DATEDIFF_BIG(nanosecond,a.StartedAtUtc,a.TerminalEndedAtUtc)/100 END AS DurationTicks
              FROM recent e LEFT JOIN attemptSummary a ON a.ExecutionId=e.ExecutionId
             ORDER BY e.CreatedAtUtc DESC,e.ExecutionId DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var observed = Utc(reader.GetDateTime(0)); var instanceId = reader.GetGuid(1);
        var queued = reader.GetInt32(2); var running = reader.GetInt32(3); var review = reader.GetInt32(4);
        var succeeded = reader.GetInt32(5); var failed = reader.GetInt32(6); var longRunning = reader.GetInt32(7); var upcoming = reader.GetInt32(8);
        var runtimeReady = reader.GetBoolean(9); var hostCeiling = reader.GetInt32(10); var heartbeat = NullableUtc(reader, 11);
        await reader.NextResultAsync(ct);
        var upcomingItems = new List<UpcomingJobDto>();
        while (await reader.ReadAsync(ct)) upcomingItems.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Utc(reader.GetDateTime(3)), "Scheduled"));
        await reader.NextResultAsync(ct);
        var recent = new List<RecentExecutionDto>();
        while (await reader.ReadAsync(ct)) recent.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(4), Utc(reader.GetDateTime(5)), reader.GetGuid(6), reader.GetString(3), reader.IsDBNull(8) ? null : TimeSpan.FromTicks(reader.GetInt64(8))));
        var overall = runtimeReady && heartbeat >= observed.AddSeconds(-30) ? "Healthy" : "Degraded";
        return new(instanceId, observed, overall, queued, running, review, succeeded, failed, longRunning, upcoming, runtimeReady, hostCeiling, heartbeat, upcomingItems, recent);
    }

    public async ValueTask<Page<JobSummaryDto>> JobsAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct)
    {
        Validate(filter, page);
        var cursor = ParseCursor(page.Cursor);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP(@take) j.JobId,j.CurrentRevision,r.Name,r.TemplateId,r.TemplateVersion,j.Enabled,j.Archived,
                CONVERT(bit,CASE WHEN s.ScheduleId IS NULL OR s.Removed=1 THEN 0 ELSE 1 END),CONVERT(bit,COALESCE(s.Paused,0)),
                (SELECT COUNT(*) FROM [workerdock].Executions e WHERE e.JobId=j.JobId AND e.State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview')),
                (SELECT COUNT(*) FROM [workerdock].Executions e WHERE e.JobId=j.JobId AND e.State IN(N'Failed',N'TimedOut') AND e.CreatedAtUtc>=DATEADD(hour,-24,SYSUTCDATETIME())),
                r.CreatedAtUtc
              FROM [workerdock].Jobs j JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision
              LEFT JOIN [workerdock].Schedules s ON s.JobId=j.JobId
             WHERE (@applyDate=0 OR (r.CreatedAtUtc>=@from AND r.CreatedAtUtc<=@to))
               AND (@search IS NULL OR r.Name LIKE @search OR r.TemplateId LIKE @search)
               AND (@template IS NULL OR r.TemplateId=@template)
               AND (@state IS NULL OR (@state=N'Enabled' AND j.Enabled=1 AND j.Archived=0) OR (@state=N'Disabled' AND j.Enabled=0 AND j.Archived=0) OR (@state=N'Archived' AND j.Archived=1))
               AND (@cursorTime IS NULL OR r.CreatedAtUtc<@cursorTime OR (r.CreatedAtUtc=@cursorTime AND j.JobId<@cursorId))
             ORDER BY r.CreatedAtUtc DESC,j.JobId DESC;
            """;
        Add(command, "@take", SqlDbType.Int, page.Limit + 1); Add(command, "@applyDate", SqlDbType.Bit, filter.ApplyDateFilter); Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc.UtcDateTime); Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc.UtcDateTime);
        Add(command, "@search", SqlDbType.NVarChar, filter.Search is null ? DBNull.Value : "%" + filter.Search + "%", 204); Add(command, "@template", SqlDbType.NVarChar, (object?)filter.TemplateId ?? DBNull.Value, 100); Add(command, "@state", SqlDbType.NVarChar, (object?)filter.State ?? DBNull.Value, 20);
        Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Value.Time.UtcDateTime); Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor is null ? DBNull.Value : cursor.Value.Id);
        var items = new List<JobSummaryDto>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt32(9), reader.GetInt32(10), Utc(reader.GetDateTime(11))));
        var hasMore = items.Count > page.Limit; if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, hasMore ? EncodeCursor(items[^1].CreatedAtUtc, items[^1].JobId) : null);
    }

    public async ValueTask<Page<ExecutionHistoryItemDto>> ExecutionsAsync(ExecutionHistoryFilter filter, PageRequest page, CancellationToken ct)
    {
        ValidateExecutionHistory(filter, page);
        var filterKey = ExecutionHistoryFilterKey(filter);
        var cursor = HistoryCursorCodec.Decode(page.Cursor, options.InstanceId, "execution-history", filterKey);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH selected AS (
                SELECT TOP(@take) e.ExecutionId,e.JobId,r.Name,r.TemplateId,e.State,e.TriggerKind,e.CreatedAtUtc
                  FROM [workerdock].Executions e
                  JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                 WHERE e.CreatedAtUtc>=@from AND e.CreatedAtUtc<@to
                   AND (@jobId IS NULL OR e.JobId=@jobId)
                   AND (@search IS NULL OR r.Name LIKE @nameSearch ESCAPE N'\' OR CONVERT(nvarchar(36),e.ExecutionId)=@search)
                   AND (@state IS NULL OR e.State=@state)
                   AND (@template IS NULL OR r.TemplateId=@template)
                   AND (@cursorTime IS NULL OR e.CreatedAtUtc<@cursorTime OR (e.CreatedAtUtc=@cursorTime AND e.ExecutionId<@cursorId))
                 ORDER BY e.CreatedAtUtc DESC,e.ExecutionId DESC
            ), attemptSummary AS (
                SELECT a.ExecutionId,MIN(a.StartedAtUtc) AS StartedAtUtc,
                       MAX(CASE WHEN a.AttemptNumber=lastAttempt.AttemptNumber THEN a.EndedAtUtc END) AS TerminalEndedAtUtc
                  FROM selected e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                  JOIN (SELECT a2.ExecutionId,MAX(a2.AttemptNumber) AS AttemptNumber FROM [workerdock].Attempts a2 JOIN selected s2 ON s2.ExecutionId=a2.ExecutionId GROUP BY a2.ExecutionId) lastAttempt
                    ON lastAttempt.ExecutionId=a.ExecutionId
                 GROUP BY a.ExecutionId
            )
            SELECT e.ExecutionId,e.JobId,e.Name,e.TemplateId,e.State,e.TriggerKind,e.CreatedAtUtc,
                   a.StartedAtUtc,CASE WHEN e.State IN(N'Succeeded',N'Failed',N'TimedOut',N'Stopped',N'Skipped') AND a.StartedAtUtc IS NOT NULL AND a.TerminalEndedAtUtc IS NOT NULL
                       THEN DATEDIFF_BIG(nanosecond,a.StartedAtUtc,a.TerminalEndedAtUtc)/100 END AS DurationTicks
              FROM selected e LEFT JOIN attemptSummary a ON a.ExecutionId=e.ExecutionId
             ORDER BY e.CreatedAtUtc DESC,e.ExecutionId DESC;
            """;
        var escapedSearch = filter.Search?.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);
        Add(command, "@take", SqlDbType.Int, page.Limit + 1);
        Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc.UtcDateTime);
        Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc.UtcDateTime);
        Add(command, "@jobId", SqlDbType.UniqueIdentifier, (object?)filter.JobId ?? DBNull.Value);
        Add(command, "@search", SqlDbType.NVarChar, (object?)filter.Search ?? DBNull.Value, 100);
        Add(command, "@nameSearch", SqlDbType.NVarChar, escapedSearch is null ? DBNull.Value : "%" + escapedSearch + "%", 204);
        Add(command, "@state", SqlDbType.NVarChar, (object?)filter.State ?? DBNull.Value, 20);
        Add(command, "@template", SqlDbType.NVarChar, (object?)filter.TemplateId ?? DBNull.Value, 100);
        Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Time.UtcDateTime);
        Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor?.Id ?? (object)DBNull.Value);
        var items = new List<ExecutionHistoryItemDto>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var state = reader.GetString(4);
                var started = NullableUtc(reader, 7);
                TimeSpan? duration = !reader.IsDBNull(8) ? TimeSpan.FromTicks(reader.GetInt64(8)) : null;
                items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), state,
                    reader.GetString(5), Utc(reader.GetDateTime(6)), started, duration));
            }
        }
        var hasMore = items.Count > page.Limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var nextCursor = hasMore ? HistoryCursorCodec.Encode(options.InstanceId, "execution-history", filterKey,
            items[^1].CreatedAtUtc.ToUniversalTime(), items[^1].ExecutionId) : null;
        return new(items, nextCursor);
    }

    public async ValueTask<ExecutionDetailsDto> ExecutionAsync(Guid executionId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.ExecutionId,e.JobId,r.Name,r.TemplateId,e.State,e.Certainty,e.TriggerKind,e.CreatedAtUtc,e.AvailableAtUtc,e.ScheduledForUtc,e.CorrelationId,e.StopRequested,e.ForceStop,r.TemplateVersion,e.JobRevision,
                CASE WHEN e.State NOT IN(N'Queued',N'RetryWaiting') OR e.StopRequested=1 OR j.Enabled=0 OR j.Archived=1
                          OR NOT EXISTS(SELECT 1 FROM [workerdock].TemplateCatalog cat WHERE cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion AND cat.Enabled=1) THEN NULL
                     WHEN e.State=N'RetryWaiting' AND e.AvailableAtUtc>SYSUTCDATETIME() THEN N'NotDue'
                     WHEN e.State=N'Queued' AND candidate.OverlapMode=N'Skip' AND j.PauseOnNeedsReview=1 AND wait.EarlierReview=1 THEN N'ReviewSkip'
                     WHEN candidate.OverlapMode<>N'Allow' AND j.PauseOnNeedsReview=1 AND wait.EarlierReview=1 THEN N'ReviewHold'
                     WHEN wait.RunningJobAttempts>0 AND (candidate.OverlapMode<>N'Allow' OR wait.ExclusiveRunning=1) THEN N'JobRunning'
                     WHEN wait.RunningJobAttempts>=j.ConcurrencyLimit THEN N'JobCapacity'
                     WHEN wait.EarlierBlocking=1 OR (candidate.OverlapMode=N'Allow' AND wait.RunningJobAttempts+wait.EarlierAllowWaiting>=j.ConcurrencyLimit) THEN N'EarlierQueued'
                     WHEN NOT EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus WHERE StateId=1 AND Ready=1 AND LastHeartbeatUtc>DATEADD(second,-30,SYSUTCDATETIME())) THEN N'WorkerUnavailable'
                     WHEN wait.RunningHostAttempts>=(SELECT HostCeiling FROM [workerdock].RuntimeStatus WHERE StateId=1) THEN N'HostCapacity'
                     ELSE NULL END AS WaitReason
              FROM [workerdock].Executions e
              JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
              JOIN [workerdock].Jobs j ON j.JobId=e.JobId
              CROSS APPLY (SELECT COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue') AS OverlapMode) candidate
              CROSS APPLY (
                    SELECT
                        (SELECT COUNT(*) FROM [workerdock].Attempts a JOIN [workerdock].Executions active ON active.ExecutionId=a.ExecutionId WHERE active.JobId=e.JobId AND a.State=N'Running') AS RunningJobAttempts,
                        (SELECT COUNT(*) FROM [workerdock].Attempts WHERE State=N'Running') AS RunningHostAttempts,
                        (SELECT COUNT(*) FROM [workerdock].Executions prior
                           JOIN [workerdock].JobRevisions priorRevision ON priorRevision.JobId=prior.JobId AND priorRevision.Revision=prior.JobRevision
                           WHERE prior.JobId=e.JobId AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence
                             AND (prior.State=N'Queued' OR (prior.State=N'RetryWaiting' AND prior.AvailableAtUtc<=SYSUTCDATETIME()))
                             AND COALESCE(JSON_VALUE(priorRevision.PolicyJson,'$.Overlap'),N'Queue')=N'Allow') AS EarlierAllowWaiting,
                        CONVERT(bit,CASE WHEN EXISTS(
                            SELECT 1 FROM [workerdock].Executions prior
                            JOIN [workerdock].JobRevisions priorRevision ON priorRevision.JobId=prior.JobId AND priorRevision.Revision=prior.JobRevision
                            WHERE prior.JobId=e.JobId AND prior.StopRequested=0 AND prior.AdmissionSequence<e.AdmissionSequence
                              AND (prior.State=N'Queued' OR (prior.State=N'RetryWaiting' AND prior.AvailableAtUtc<=SYSUTCDATETIME()))
                              AND COALESCE(JSON_VALUE(priorRevision.PolicyJson,'$.Overlap'),N'Queue')<>N'Allow'
                              AND (COALESCE(JSON_VALUE(priorRevision.PolicyJson,'$.Overlap'),N'Queue')<>N'Skip'
                                   OR (prior.HoldsJobSlot=1
                                       AND NOT (j.PauseOnNeedsReview=1 AND EXISTS(SELECT 1 FROM [workerdock].Executions reviewPrior
                                           WHERE reviewPrior.JobId=e.JobId AND reviewPrior.State=N'NeedsReview' AND reviewPrior.AdmissionSequence<prior.AdmissionSequence)))))
                        THEN 1 ELSE 0 END) AS EarlierBlocking,
                        CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Executions prior
                            WHERE prior.JobId=e.JobId AND prior.State=N'NeedsReview' AND prior.AdmissionSequence<e.AdmissionSequence)
                            THEN 1 ELSE 0 END) AS EarlierReview,
                        CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Attempts a
                            JOIN [workerdock].Executions active ON active.ExecutionId=a.ExecutionId
                            JOIN [workerdock].JobRevisions activeRevision ON activeRevision.JobId=active.JobId AND activeRevision.Revision=active.JobRevision
                            WHERE active.JobId=e.JobId AND a.State=N'Running' AND COALESCE(JSON_VALUE(activeRevision.PolicyJson,'$.Overlap'),N'Queue')<>N'Allow')
                            THEN 1 ELSE 0 END) AS ExclusiveRunning
              ) wait
             WHERE e.ExecutionId=@id;
            SELECT a.AttemptId,a.AttemptNumber,a.State,a.StartedAtUtc,a.EndedAtUtc,a.DeadlineUtc,a.SafeCode,a.LocalExitConfirmed,a.EffectsMayHaveOccurred,d.DiagnosticJson
              FROM [workerdock].Attempts a LEFT JOIN [workerdock].AttemptFailureDiagnostics d ON d.AttemptId=a.AttemptId WHERE a.ExecutionId=@id ORDER BY a.AttemptNumber;
            SELECT EventId,AttemptId,Code,AtUtc FROM [workerdock].OperationalEvents WHERE ExecutionId=@id AND EventKind=N'Execution' AND Source=N'Lifecycle' ORDER BY AtUtc,EventId;
            """;
        Add(command, "@id", SqlDbType.UniqueIdentifier, executionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("ExecutionNotFound");
        var detail = new ExecutionCore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), Utc(reader.GetDateTime(7)), Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : Utc(reader.GetDateTime(9)), reader.GetGuid(10), reader.GetBoolean(11), reader.GetBoolean(12), reader.GetString(13), reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetString(15));
        await reader.NextResultAsync(ct);
        var attempts = new List<ExecutionAttemptDto>();
        while (await reader.ReadAsync(ct)) attempts.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), NullableUtc(reader, 3), NullableUtc(reader, 4), NullableUtc(reader, 5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<FailureDiagnostic>(reader.GetString(9))));
        await reader.NextResultAsync(ct);
        var timeline = new List<ExecutionEventDto>();
        while (await reader.ReadAsync(ct)) timeline.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), Utc(reader.GetDateTime(3))));
        return new(detail.ExecutionId, detail.JobId, detail.JobName, detail.TemplateId, detail.State, detail.Certainty, detail.TriggerKind, detail.CreatedAtUtc, detail.AvailableAtUtc, detail.ScheduledForUtc, detail.CorrelationId, detail.StopRequested, detail.ForceStop, attempts, timeline, detail.TemplateVersion, detail.JobRevision, detail.WaitReason);
    }

    public async ValueTask<Page<AuditSummaryDto>> AuditAsync(AuditHistoryFilter filter, PageRequest page, CancellationToken ct)
    {
        ValidateAuditHistory(filter, page);
        var filterKey = AuditHistoryFilterKey(filter);
        var cursor = HistoryCursorCodec.Decode(page.Cursor, options.InstanceId, "audit-history", filterKey);
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP(@take) EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc
              FROM [workerdock].AuditEvents
             WHERE OccurredAtUtc>=@from AND OccurredAtUtc<@to
               AND (@eventType IS NULL OR EventType=@eventType)
               AND (@search IS NULL OR EventType LIKE @search ESCAPE N'\' OR ActorSubject LIKE @search ESCAPE N'\' OR CONVERT(nvarchar(36),CorrelationId)=@plainSearch)
               AND (@cursorTime IS NULL OR OccurredAtUtc<@cursorTime OR (OccurredAtUtc=@cursorTime AND EventId<@cursorId))
             ORDER BY OccurredAtUtc DESC,EventId DESC;
            """;
        var escapedSearch = filter.Search?.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);
        Add(command, "@take", SqlDbType.Int, page.Limit + 1); Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc.UtcDateTime); Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc.UtcDateTime);
        Add(command, "@eventType", SqlDbType.NVarChar, (object?)filter.EventType ?? DBNull.Value, 100);
        Add(command, "@search", SqlDbType.NVarChar, escapedSearch is null ? DBNull.Value : "%" + escapedSearch + "%", 204); Add(command, "@plainSearch", SqlDbType.NVarChar, (object?)filter.Search ?? DBNull.Value, 100);
        Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Time.UtcDateTime); Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor?.Id ?? (object)DBNull.Value);
        var items = new List<AuditSummaryDto>();
        await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) items.Add(AuditEventProjection.Project(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4), Utc(reader.GetDateTime(5)))));
        var hasMore = items.Count > page.Limit; if (hasMore) items.RemoveAt(items.Count - 1);
        var last = items.LastOrDefault();
        return new(items, hasMore && last is not null ? HistoryCursorCodec.Encode(options.InstanceId, "audit-history", filterKey, last.OccurredAtUtc, last.EventId) : null);
    }

    public async ValueTask<IReadOnlyList<string>> AuditEventTypesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT EventType FROM [workerdock].AuditEvents ORDER BY EventType;";
        var eventTypes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) eventTypes.Add(reader.GetString(0));
        return eventTypes;
    }

    public async ValueTask<RuntimeStatusDto> WorkersAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT BootId,Ready,HostCeiling,LastHeartbeatUtc FROM [workerdock].RuntimeStatus WHERE StateId=1;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(null, false, 1, null);
        Guid? bootId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        var heartbeat = NullableUtc(reader, 3);
        var ready = reader.GetBoolean(1) && heartbeat >= clock.GetUtcNow().AddSeconds(-30);
        var ceiling = reader.GetInt32(2);
        if (heartbeat is not null) TelemetryRegistration.RecordHeartbeatAge(clock.GetUtcNow() - heartbeat.Value);
        return new(bootId, ready, ceiling, heartbeat);
    }

    public async ValueTask<TenantHealthDto> HealthAsync(CancellationToken ct)
    {
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web"] = "Healthy", ["database"] = "Unavailable", ["runtime"] = "Unavailable"
        };
        var observed = clock.GetUtcNow();
        try
        {
            var status = await new SqlRuntimeStatus(options, 1).ReadAsync(ct);
            components["database"] = "Healthy";
            components["runtime"] = status is { Ready: true } &&
                status.LastHeartbeatUtc >= observed.AddSeconds(-30) ? "Healthy" : "Unavailable";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* Readiness is closed when SQL or the status row cannot be read. */ }
        var overall = components["runtime"] == "Healthy" ? "Healthy" : "Unavailable";
        return new(options.InstanceId, overall, components, observed);
    }

    private async ValueTask<SqlConnection> OpenAsync(CancellationToken ct)
    {
        try
        {
            var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct);
            await using var identity = connection.CreateCommand(); identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
            if ((Guid?)await identity.ExecuteScalarAsync(ct) != options.InstanceId) { await connection.DisposeAsync(); throw new WorkerDockException("TenantInstanceMismatch"); }
            return connection;
        }
        catch (WorkerDockException) { throw; }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static async ValueTask<DateTimeOffset> DatabaseNowAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT SYSUTCDATETIME()";
        return Utc((DateTime)(await command.ExecuteScalarAsync(ct))!);
    }

    private static readonly string[] ExecutionStates = ["Queued", "Running", "RetryWaiting", "NeedsReview", "Succeeded", "Failed", "TimedOut", "Stopped", "Skipped"];

    private static void ValidateExecutionHistory(ExecutionHistoryFilter filter, PageRequest page)
    {
        if (page.Limit is < 1 or > 100 ||
            filter.ToUtc < filter.FromUtc || filter.ToUtc - filter.FromUtc > MaxRange ||
            filter.Search is { Length: > 100 } || filter.TemplateId is { Length: > 100 } ||
            filter.State is not null && !ExecutionStates.Contains(filter.State, StringComparer.Ordinal))
            throw new WorkerDockException("ValidationFailed");
    }

    private static void ValidateAuditHistory(AuditHistoryFilter filter, PageRequest page)
    {
        if (page.Limit is < 1 or > 100 || filter.ToUtc <= filter.FromUtc || filter.ToUtc - filter.FromUtc > MaxAuditRange ||
            filter.Search is { Length: > 100 } || filter.EventType is { Length: > 100 })
            throw new WorkerDockException("ValidationFailed");
    }

    private static string AuditHistoryFilterKey(AuditHistoryFilter filter) => JsonSerializer.Serialize(new
    {
        fromUtc = filter.FromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        toUtc = filter.ToUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        search = filter.Search,
        eventType = filter.EventType
    });

    private static string ExecutionHistoryFilterKey(ExecutionHistoryFilter filter) => JsonSerializer.Serialize(new
    {
        fromUtc = filter.FromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        toUtc = filter.ToUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        search = filter.Search,
        state = filter.State,
        templateId = filter.TemplateId,
        jobId = filter.JobId
    });

    private static void Validate(MonitoringFilter filter, PageRequest page)
    {
        if (page.Limit is < 1 or > 100 || filter.ToUtc < filter.FromUtc || filter.ToUtc - filter.FromUtc > MaxRange ||
            filter.Search is { Length: > 100 } || filter.TemplateId is { Length: > 100 } ||
            filter.State is not (null or "Enabled" or "Disabled" or "Archived")) throw new WorkerDockException("ValidationFailed");
    }
    private static (DateTimeOffset Time, Guid Id)? ParseCursor(string? value)
    {
        if (value is null) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out var id)) throw new FormatException();
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch { throw new WorkerDockException("ValidationFailed"); }
    }
    private static string EncodeCursor(DateTimeOffset time, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(time.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + id.ToString("N")));
    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0) { var parameter = command.Parameters.Add(name, type); if (size != 0) parameter.Size = size; parameter.Value = value; }
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? NullableUtc(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Utc(reader.GetDateTime(ordinal));
    private sealed record ExecutionCore(Guid ExecutionId, Guid JobId, string JobName, string TemplateId, string State, string Certainty, string TriggerKind, DateTimeOffset CreatedAtUtc, DateTimeOffset AvailableAtUtc, DateTimeOffset? ScheduledForUtc, Guid CorrelationId, bool StopRequested, bool ForceStop, string TemplateVersion, long JobRevision, string? WaitReason);
}
