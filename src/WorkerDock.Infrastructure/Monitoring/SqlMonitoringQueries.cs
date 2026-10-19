using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Monitoring;

public sealed class SqlMonitoringQueries(TenantDatabaseOptions options, TimeProvider clock) : IMonitoringQueries
{
    private static readonly string[] ActiveStates = ["Queued", "Running", "RetryWaiting", "NeedsReview"];
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(90);

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
                (SELECT COUNT(*) FROM [workerdock].Workers WHERE State=N'Ready' AND LastHeartbeatUtc>=DATEADD(second,-60,@now)),
                (SELECT COUNT(*) FROM [workerdock].Workers),
                COALESCE((SELECT DesiredRevision FROM [workerdock].PolicyRevisions WHERE PolicyKind=N'Capacity'),0),
                COALESCE((SELECT EffectiveRevision FROM [workerdock].PolicyRevisions WHERE PolicyKind=N'Capacity'),0),
                COALESCE((SELECT State FROM [workerdock].PolicyRevisions WHERE PolicyKind=N'Capacity'),N'Unavailable'),
                (SELECT COUNT(*) FROM [workerdock].Outbox WHERE ProjectionState IN(N'Pending',N'Held') AND CreatedAtUtc<DATEADD(second,-30,@now));
            SELECT TOP(10) j.JobId,r.Name,r.TemplateId,s.OneTimeUtc
              FROM [workerdock].Schedules s JOIN [workerdock].Jobs j ON j.JobId=s.JobId
              JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision
             WHERE s.Removed=0 AND s.Paused=0 AND j.Enabled=1 AND s.Kind=N'Once' AND s.OneTimeUtc>SYSUTCDATETIME()
             ORDER BY s.OneTimeUtc,s.ScheduleId;
            SELECT TOP(10) e.ExecutionId,e.JobId,r.Name,e.State,e.CreatedAtUtc,e.CorrelationId
              FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
             ORDER BY e.CreatedAtUtc DESC,e.ExecutionId DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var observed = Utc(reader.GetDateTime(0)); var instanceId = reader.GetGuid(1);
        var queued = reader.GetInt32(2); var running = reader.GetInt32(3); var review = reader.GetInt32(4);
        var succeeded = reader.GetInt32(5); var failed = reader.GetInt32(6); var longRunning = reader.GetInt32(7); var upcoming = reader.GetInt32(8);
        var readyWorkers = reader.GetInt32(9); var totalWorkers = reader.GetInt32(10); var desired = reader.GetInt64(11); var effective = reader.GetInt64(12);
        var capacityState = reader.GetString(13); var pending = reader.GetInt32(14);
        await reader.NextResultAsync(ct);
        var upcomingItems = new List<UpcomingJobDto>();
        while (await reader.ReadAsync(ct)) upcomingItems.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Utc(reader.GetDateTime(3)), "Scheduled"));
        await reader.NextResultAsync(ct);
        var recent = new List<RecentExecutionDto>();
        while (await reader.ReadAsync(ct)) recent.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), Utc(reader.GetDateTime(4)), reader.GetGuid(5)));
        var overall = readyWorkers == 0 || capacityState != "Effective" || pending > 0 ? "Degraded" : "Healthy";
        return new(instanceId, observed, overall, queued, running, review, succeeded, failed, longRunning, upcoming, readyWorkers, totalWorkers, desired, effective, capacityState, pending, upcomingItems, recent);
    }

    public async ValueTask<Page<JobSummaryDto>> JobsAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct)
    {
        Validate(filter, page);
        var cursor = ParseCursor(page.Cursor);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP(@take) j.JobId,j.CurrentRevision,r.Name,r.TemplateId,r.TemplateVersion,j.Enabled,j.Archived,
                CONVERT(bit,CASE WHEN s.ScheduleId IS NULL OR s.Removed=1 THEN 0 ELSE 1 END),COALESCE(s.Paused,0),
                (SELECT COUNT(*) FROM [workerdock].Executions e WHERE e.JobId=j.JobId AND e.State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview')),
                (SELECT COUNT(*) FROM [workerdock].Executions e WHERE e.JobId=j.JobId AND e.State IN(N'Failed',N'TimedOut') AND e.CreatedAtUtc>=DATEADD(hour,-24,SYSUTCDATETIME())),
                r.CreatedAtUtc
              FROM [workerdock].Jobs j JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision
              LEFT JOIN [workerdock].Schedules s ON s.JobId=j.JobId
             WHERE r.CreatedAtUtc>=@from AND r.CreatedAtUtc<=@to
               AND (@search IS NULL OR r.Name LIKE @search OR r.TemplateId LIKE @search)
               AND (@template IS NULL OR r.TemplateId=@template)
               AND (@state IS NULL OR (@state=N'Enabled' AND j.Enabled=1 AND j.Archived=0) OR (@state=N'Disabled' AND j.Enabled=0 AND j.Archived=0) OR (@state=N'Archived' AND j.Archived=1))
               AND (@cursorTime IS NULL OR r.CreatedAtUtc<@cursorTime OR (r.CreatedAtUtc=@cursorTime AND j.JobId<@cursorId))
             ORDER BY r.CreatedAtUtc DESC,j.JobId DESC;
            """;
        Add(command, "@take", SqlDbType.Int, page.Limit + 1); Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc.UtcDateTime); Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc.UtcDateTime);
        Add(command, "@search", SqlDbType.NVarChar, filter.Search is null ? DBNull.Value : "%" + filter.Search + "%", 204); Add(command, "@template", SqlDbType.NVarChar, (object?)filter.TemplateId ?? DBNull.Value, 100); Add(command, "@state", SqlDbType.NVarChar, (object?)filter.State ?? DBNull.Value, 20);
        Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Value.Time.UtcDateTime); Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor is null ? DBNull.Value : cursor.Value.Id);
        var items = new List<JobSummaryDto>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt32(9), reader.GetInt32(10), Utc(reader.GetDateTime(11))));
        var hasMore = items.Count > page.Limit; if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, hasMore ? EncodeCursor(items[^1].CreatedAtUtc, items[^1].JobId) : null);
    }

    public async ValueTask<ExecutionDetailsDto> ExecutionAsync(Guid executionId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.ExecutionId,e.JobId,r.Name,r.TemplateId,e.State,e.Certainty,e.TriggerKind,e.CreatedAtUtc,e.AvailableAtUtc,e.ScheduledForUtc,e.CorrelationId,e.StopRequested,e.ForceStop
              FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision WHERE e.ExecutionId=@id;
            SELECT a.AttemptId,a.AttemptNumber,a.InstallationId,a.State,a.StartedAtUtc,a.EndedAtUtc,a.DeadlineUtc,a.SafeCode,a.LocalExitConfirmed,a.EffectsMayHaveOccurred
              FROM [workerdock].Attempts a WHERE a.ExecutionId=@id ORDER BY a.AttemptNumber;
            SELECT EventId,AttemptId,State,OccurredAtUtc FROM [workerdock].AttemptStatusEvents WHERE ExecutionId=@id ORDER BY OccurredAtUtc,EventId;
            """;
        Add(command, "@id", SqlDbType.UniqueIdentifier, executionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("ExecutionNotFound");
        var detail = new ExecutionCore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), Utc(reader.GetDateTime(7)), Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : Utc(reader.GetDateTime(9)), reader.GetGuid(10), reader.GetBoolean(11), reader.GetBoolean(12));
        await reader.NextResultAsync(ct);
        var attempts = new List<ExecutionAttemptDto>();
        while (await reader.ReadAsync(ct)) attempts.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), NullableUtc(reader, 4), NullableUtc(reader, 5), NullableUtc(reader, 6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        await reader.NextResultAsync(ct);
        var timeline = new List<ExecutionEventDto>();
        while (await reader.ReadAsync(ct)) timeline.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), Utc(reader.GetDateTime(3))));
        return new(detail.ExecutionId, detail.JobId, detail.JobName, detail.TemplateId, detail.State, detail.Certainty, detail.TriggerKind, detail.CreatedAtUtc, detail.AvailableAtUtc, detail.ScheduledForUtc, detail.CorrelationId, detail.StopRequested, detail.ForceStop, attempts, timeline);
    }

    public async ValueTask<Page<AuditSummaryDto>> AuditAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct)
    {
        Validate(filter, page); var cursor = ParseCursor(page.Cursor);
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP(@take) EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc
              FROM [workerdock].AuditEvents
             WHERE OccurredAtUtc>=@from AND OccurredAtUtc<=@to
               AND (@search IS NULL OR EventType LIKE @search OR ActorSubject LIKE @search OR CONVERT(nvarchar(36),CorrelationId)=@plainSearch)
               AND (@cursorTime IS NULL OR OccurredAtUtc<@cursorTime OR (OccurredAtUtc=@cursorTime AND EventId<@cursorId))
             ORDER BY OccurredAtUtc DESC,EventId DESC;
            """;
        Add(command, "@take", SqlDbType.Int, page.Limit + 1); Add(command, "@from", SqlDbType.DateTime2, filter.FromUtc.UtcDateTime); Add(command, "@to", SqlDbType.DateTime2, filter.ToUtc.UtcDateTime);
        Add(command, "@search", SqlDbType.NVarChar, filter.Search is null ? DBNull.Value : "%" + filter.Search + "%", 204); Add(command, "@plainSearch", SqlDbType.NVarChar, (object?)filter.Search ?? DBNull.Value, 100);
        Add(command, "@cursorTime", SqlDbType.DateTime2, cursor is null ? DBNull.Value : cursor.Value.Time.UtcDateTime); Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor is null ? DBNull.Value : cursor.Value.Id);
        var items = new List<AuditSummaryDto>();
        await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4), Utc(reader.GetDateTime(5))));
        var hasMore = items.Count > page.Limit; if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, hasMore ? EncodeCursor(items[^1].OccurredAtUtc, items[^1].EventId) : null);
    }

    public async ValueTask<IReadOnlyList<WorkerSummaryDto>> WorkersAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP(200) w.InstallationId,w.State,w.CapacityCeiling,w.LiveLimit,w.AcknowledgedRevision,w.LastHeartbeatUtc,w.ReservationsRecovered,c.TemplateId,c.TemplateVersion
              FROM [workerdock].Workers w LEFT JOIN [workerdock].WorkerCapabilities c ON c.InstallationId=w.InstallationId
             ORDER BY w.LastHeartbeatUtc DESC,w.InstallationId,c.TemplateId,c.TemplateVersion;
            """;
        var rows = new Dictionary<Guid, WorkerBuilder>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            if (!rows.TryGetValue(id, out var row)) rows[id] = row = new(id, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4), Utc(reader.GetDateTime(5)), reader.GetBoolean(6));
            if (!reader.IsDBNull(7)) row.Capabilities.Add(reader.GetString(7) + "@" + reader.GetString(8));
        }
        return rows.Values.Select(x => new WorkerSummaryDto(x.Id, x.State, x.Ceiling, x.LiveLimit, x.Revision, x.Heartbeat, x.Recovered, x.Capabilities)).ToArray();
    }

    public async ValueTask<TenantHealthDto> HealthAsync(CancellationToken ct)
    {
        var components = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["web"] = "Healthy", ["database"] = "Unknown", ["schema"] = "Unknown", ["schedulerStore"] = "Unknown",
            ["executionStore"] = "Unknown", ["scheduler"] = "Unknown", ["workers"] = "Unknown", ["catalog"] = "Unknown",
            ["capacity"] = "Unknown", ["projections"] = "Unknown"
        };
        var observed = clock.GetUtcNow();
        try
        {
            await using var connection = await OpenAsync(ct); observed = await DatabaseNowAsync(connection, ct);
            components["database"] = "Healthy";
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM [workerdock].SchemaHistory),
                    (SELECT COUNT(*) FROM [workerdock].ServiceHealth WHERE ServiceKind=N'Scheduler' AND Ready=1 AND LastHeartbeatUtc>=DATEADD(second,-90,SYSUTCDATETIME())),
                    (SELECT COUNT(*) FROM [workerdock].Workers WHERE State=N'Ready' AND LastHeartbeatUtc>=DATEADD(second,-60,SYSUTCDATETIME())),
                    (SELECT COUNT(*) FROM [workerdock].TemplateCatalog WHERE Enabled=1),
                    COALESCE((SELECT State FROM [workerdock].PolicyRevisions WHERE PolicyKind=N'Capacity'),N'Unavailable'),
                    (SELECT COUNT(*) FROM [workerdock].Outbox WHERE ProjectionState IN(N'Pending',N'Held') AND CreatedAtUtc<DATEADD(second,-30,SYSUTCDATETIME())),
                    (SELECT COUNT(*) FROM [workerdock].WDS_QRTZ_SCHEDULER_STATE),
                    (SELECT COUNT(*) FROM [workerdock].WDE_QRTZ_SCHEDULER_STATE);
                """;
            await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            components["schema"] = reader.GetInt32(0) > 0 ? "Healthy" : "Unavailable";
            components["scheduler"] = reader.GetInt32(1) > 0 ? "Healthy" : "Unavailable";
            components["workers"] = reader.GetInt32(2) > 0 ? "Healthy" : "Degraded";
            components["catalog"] = reader.GetInt32(3) > 0 ? "Healthy" : "Degraded";
            components["capacity"] = reader.GetString(4) == "Effective" ? "Healthy" : "Degraded";
            components["projections"] = reader.GetInt32(5) == 0 ? "Healthy" : "Degraded";
            components["schedulerStore"] = reader.GetInt32(6) >= 0 ? "Healthy" : "Unavailable";
            components["executionStore"] = reader.GetInt32(7) >= 0 ? "Healthy" : "Unavailable";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { components["database"] = "Unavailable"; }
        var overall = components.Values.Any(x => x == "Unavailable") ? "Unavailable" : components.Values.Any(x => x == "Degraded") ? "Degraded" : "Healthy";
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
    private sealed record ExecutionCore(Guid ExecutionId, Guid JobId, string JobName, string TemplateId, string State, string Certainty, string TriggerKind, DateTimeOffset CreatedAtUtc, DateTimeOffset AvailableAtUtc, DateTimeOffset? ScheduledForUtc, Guid CorrelationId, bool StopRequested, bool ForceStop);
    private sealed class WorkerBuilder(Guid id, string state, int ceiling, int liveLimit, long revision, DateTimeOffset heartbeat, bool recovered)
    {
        public Guid Id { get; } = id; public string State { get; } = state; public int Ceiling { get; } = ceiling; public int LiveLimit { get; } = liveLimit; public long Revision { get; } = revision; public DateTimeOffset Heartbeat { get; } = heartbeat; public bool Recovered { get; } = recovered; public List<string> Capabilities { get; } = [];
    }
}
