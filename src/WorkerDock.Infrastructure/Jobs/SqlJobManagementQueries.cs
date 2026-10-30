using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Monitoring;
using WorkerDock.Domain.Jobs;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Scheduling;

namespace WorkerDock.Infrastructure.Jobs;

public sealed class SqlJobManagementQueries(TenantDatabaseOptions options) : IJobManagementQueries
{
    private const string NameOrder = "Latin1_General_100_CI_AS_SC";
    private sealed record ListCursor(string Scope, string Name, Guid JobId);
    private sealed record HistoryCursor(Guid InstanceId, Guid JobId, DateTimeOffset CreatedAtUtc, Guid ExecutionId);

    public async ValueTask<Page<JobListItemDto>> ListAsync(JobListFilter filter, PageRequest page, CancellationToken ct)
    {
        ValidatePage(page);
        if (filter.View is not ("current" or "enabled" or "disabled" or "archived")) throw new WorkerDockException("ValidationFailed", "view");
        if (filter.Search?.Length > 100) throw new WorkerDockException("ValidationFailed", "search");
        var search = filter.Search ?? "";
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.InstanceId + "\n" + filter.View + "\n" + search)));
        var cursor = Decode<ListCursor>(page.Cursor);
        if (cursor is not null && (cursor.Scope != scope || cursor.Name is null || cursor.Name.Length > 200 || cursor.JobId == Guid.Empty))
            throw new WorkerDockException("ValidationFailed", "cursor");
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct);
            await CheckInstanceAsync(connection, null, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT TOP(@take) j.JobId,r.Name,r.TemplateId,r.TemplateVersion,j.Enabled,j.Archived,
                    s.Kind,s.ZoneId,COALESCE(s.Paused,CAST(0 AS bit)),r.CreatedAtUtc
                FROM [workerdock].Jobs j
                JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision
                LEFT JOIN [workerdock].Schedules s ON s.JobId=j.JobId AND s.Removed=0
                WHERE ((@view=N'archived' AND j.Archived=1) OR
                    (j.Archived=0 AND (@view=N'current' OR (@view=N'enabled' AND j.Enabled=1) OR (@view=N'disabled' AND j.Enabled=0))))
                  AND (@search=N'' OR CHARINDEX(@search,r.Name)>0 OR CHARINDEX(@search,r.TemplateId)>0)
                  AND (@lastName IS NULL OR r.Name COLLATE {NameOrder}>@lastName COLLATE {NameOrder}
                    OR (r.Name COLLATE {NameOrder}=@lastName COLLATE {NameOrder} AND j.JobId>@lastId))
                ORDER BY r.Name COLLATE {NameOrder},j.JobId
                """;
            command.Parameters.Add(new("@take", SqlDbType.Int) { Value = page.Limit + 1 });
            command.Parameters.Add(new("@view", SqlDbType.NVarChar, 16) { Value = filter.View });
            command.Parameters.Add(new("@search", SqlDbType.NVarChar, 100) { Value = search });
            command.Parameters.Add(new("@lastName", SqlDbType.NVarChar, 200) { Value = cursor?.Name ?? (object)DBNull.Value });
            command.Parameters.Add(new("@lastId", SqlDbType.UniqueIdentifier) { Value = cursor?.JobId ?? Guid.Empty });
            var items = new List<JobListItemDto>(); await using var row = await command.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) items.Add(new(row.GetGuid(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetBoolean(4), row.GetBoolean(5), row.IsDBNull(6) ? null : row.GetString(6), row.IsDBNull(7) ? null : row.GetString(7), row.GetBoolean(8), Utc(row.GetDateTime(9))));
            var more = items.Count > page.Limit; if (more) items.RemoveAt(items.Count - 1);
            return new(items, more ? Encode(new ListCursor(scope, items[^1].Name, items[^1].JobId)) : null);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<JobManagementDetailDto> GetAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await CheckInstanceAsync(connection, transaction, ct);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT j.CurrentRevision,j.Version,j.Enabled,j.Archived,j.ConcurrencyLimit,
                    r.Name,r.TemplateId,r.TemplateVersion,r.ConfigurationJson,r.ProfileId,r.PolicyJson,r.DynamicValuesJson,r.HideFailureDetails,
                    s.ScheduleId,s.Revision,s.Kind,s.Cron,s.ZoneId,s.OneTimeUtc,s.ActivatedUtc,s.Paused,s.Removed,
                    s.MissedMode,s.LatenessToleranceMs,s.CatchUpCount,s.CatchUpAgeMs,j.PauseOnNeedsReview
                FROM [workerdock].Jobs j
                JOIN [workerdock].JobRevisions r ON r.JobId=j.JobId AND r.Revision=j.CurrentRevision
                LEFT JOIN [workerdock].Schedules s ON s.JobId=j.JobId WHERE j.JobId=@job
                """;
            command.Parameters.Add(new("@job", SqlDbType.UniqueIdentifier) { Value = jobId });
            await using var row = await command.ExecuteReaderAsync(ct);
            if (!await row.ReadAsync(ct)) throw new WorkerDockException("JobNotFound");
            var revision = new JobRevision(jobId, row.GetInt64(0), row.GetString(5), row.GetString(6), row.GetString(7), JsonDocument.Parse(row.GetString(8)).RootElement.Clone(), row.IsDBNull(9) ? null : row.GetGuid(9), JsonSerializer.Deserialize<ExecutionPolicy>(row.GetString(10))!, row.IsDBNull(11) ? null : JsonDocument.Parse(row.GetString(11)).RootElement.Clone(), row.GetBoolean(12));
            var editor = new JobEditorDto(revision, row.GetFieldValue<byte[]>(1), row.GetBoolean(2), row.GetBoolean(3), row.GetInt32(4), row.GetBoolean(26));
            WorkerDock.Application.Scheduling.ScheduleEditorInput? schedule = null, previous = null;
            MissedWorkPolicy? policy = null; var paused = false;
            if (!row.IsDBNull(13))
            {
                var definition = new ScheduleDefinition(row.GetGuid(13), row.GetInt64(14), row.GetString(15), row.IsDBNull(16) ? null : row.GetString(16), row.GetString(17), row.IsDBNull(18) ? null : Utc(row.GetDateTime(18)), Utc(row.GetDateTime(19)));
                var input = ScheduleInputConverter.ToInput(definition);
                if (row.GetBoolean(21)) previous = input;
                else
                {
                    schedule = input; paused = row.GetBoolean(20);
                    policy = new(row.GetString(22), TimeSpan.FromMilliseconds(row.GetInt64(23)), row.GetInt32(24), TimeSpan.FromMilliseconds(row.GetInt64(25)));
                }
            }
            await row.CloseAsync(); await transaction.CommitAsync(ct);
            return new(editor, schedule, policy, paused, previous);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<Page<JobExecutionItemDto>> ExecutionsAsync(Guid jobId, PageRequest page, CancellationToken ct)
    {
        ValidatePage(page);
        var cursor = Decode<HistoryCursor>(page.Cursor);
        if (cursor is not null && (cursor.InstanceId != options.InstanceId || cursor.JobId != jobId || cursor.ExecutionId == Guid.Empty || cursor.CreatedAtUtc.Offset != TimeSpan.Zero))
            throw new WorkerDockException("ValidationFailed", "cursor");
        try
        {
            await using var connection = new SqlConnection(options.ConnectionString); await connection.OpenAsync(ct);
            await CheckInstanceAsync(connection, null, ct);
            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM [workerdock].Jobs WHERE JobId=@job";
                exists.Parameters.Add(new("@job", SqlDbType.UniqueIdentifier) { Value = jobId });
                if (await exists.ExecuteScalarAsync(ct) is null) throw new WorkerDockException("JobNotFound");
            }
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP(@take) e.ExecutionId,e.JobRevision,e.TriggerKind,e.State,e.CreatedAtUtc,
                    COALESCE(e.FirstStartedAtUtc,a.StartedAtUtc),a.EndedAtUtc
                FROM [workerdock].Executions e
                OUTER APPLY (SELECT MIN(StartedAtUtc) AS StartedAtUtc,MAX(EndedAtUtc) AS EndedAtUtc FROM [workerdock].Attempts WHERE ExecutionId=e.ExecutionId) a
                WHERE e.JobId=@job AND (@lastTime IS NULL OR e.CreatedAtUtc<@lastTime OR (e.CreatedAtUtc=@lastTime AND e.ExecutionId<@lastId))
                ORDER BY e.CreatedAtUtc DESC,e.ExecutionId DESC
                """;
            command.Parameters.Add(new("@job", SqlDbType.UniqueIdentifier) { Value = jobId });
            command.Parameters.Add(new("@take", SqlDbType.Int) { Value = page.Limit + 1 });
            command.Parameters.Add(new("@lastTime", SqlDbType.DateTime2) { Value = cursor?.CreatedAtUtc.UtcDateTime ?? (object)DBNull.Value });
            command.Parameters.Add(new("@lastId", SqlDbType.UniqueIdentifier) { Value = cursor?.ExecutionId ?? Guid.Empty });
            var items = new List<JobExecutionItemDto>(); await using var row = await command.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct))
            {
                var started = row.IsDBNull(5) ? (DateTimeOffset?)null : Utc(row.GetDateTime(5));
                var duration = started is { } start && !row.IsDBNull(6) && row.GetString(3) is ("Succeeded" or "Failed" or "Stopped" or "TimedOut" or "Skipped") ? Utc(row.GetDateTime(6)) - start : (TimeSpan?)null;
                items.Add(new(row.GetGuid(0), row.GetInt64(1), row.GetString(2), row.GetString(3), Utc(row.GetDateTime(4)), started, duration));
            }
            var more = items.Count > page.Limit; if (more) items.RemoveAt(items.Count - 1);
            return new(items, more ? Encode(new HistoryCursor(options.InstanceId, jobId, items[^1].CreatedAtUtc, items[^1].ExecutionId)) : null);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static void ValidatePage(PageRequest page)
    {
        if (page.Limit is < 1 or > 100) throw new WorkerDockException("ValidationFailed", "limit");
        if (page.Cursor?.Length > 2048) throw new WorkerDockException("ValidationFailed", "cursor");
    }
    private static T? Decode<T>(string? cursor) where T : class
    {
        if (cursor is null) return null;
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(cursor)) ?? throw new JsonException(); }
        catch (Exception error) when (error is FormatException or JsonException) { throw new WorkerDockException("ValidationFailed", "cursor"); }
    }
    private static string Encode<T>(T cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));
    private async ValueTask CheckInstanceAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
    }
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class UnavailableJobManagementQueries : IJobManagementQueries
{
    public ValueTask<Page<JobListItemDto>> ListAsync(JobListFilter filter, PageRequest page, CancellationToken ct) => ValueTask.FromException<Page<JobListItemDto>>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<JobManagementDetailDto> GetAsync(Guid jobId, CancellationToken ct) => ValueTask.FromException<JobManagementDetailDto>(new WorkerDockException("PersistenceUnavailable"));
    public ValueTask<Page<JobExecutionItemDto>> ExecutionsAsync(Guid jobId, PageRequest page, CancellationToken ct) => ValueTask.FromException<Page<JobExecutionItemDto>>(new WorkerDockException("PersistenceUnavailable"));
}
