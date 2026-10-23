using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class IncidentEvaluator(TenantDatabaseOptions options) : IAlertEvaluator
{

    public async ValueTask<int> EvaluateAsync(DateTimeOffset throughUtc, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                var sqlConnection = (SqlConnection)connection;
                var sqlTransaction = (SqlTransaction)transaction;
                await VerifyTenantAsync(sqlConnection, sqlTransaction, token);
                var sqlNow = await ReadSqlNowAsync(sqlConnection, sqlTransaction, token);
                var requestedThrough = throughUtc.UtcDateTime;
                var now = requestedThrough < sqlNow ? requestedThrough : sqlNow;
                var cursor = await ReadCursorAsync(sqlConnection, sqlTransaction, token);
                var rules = await ReadRulesAsync(sqlConnection, sqlTransaction, cursor.RuleId, cursor.SubjectKey is not null, Math.Min(limit, 200), token);
                var processed = 0;
                foreach (var rule in rules)
                {
                    token.ThrowIfCancellationRequested();
                    var afterSubject = cursor.RuleId == rule.Id ? cursor.SubjectKey : null;
                    var observations = rule.Enabled
                        ? await ReadObservationsAsync(sqlConnection, sqlTransaction, rule, now, afterSubject, limit, token)
                        : new Dictionary<string, DateTime>(StringComparer.Ordinal);
                    await ReconcileRuleAsync(sqlConnection, sqlTransaction, rule, observations, now, token);
                    var hasMoreSubjects = rule.Enabled && observations.Count == limit;
                    var lastSubject = hasMoreSubjects ? observations.Keys.Order(StringComparer.Ordinal).Last() : null;
                    await using var updateCursor = Command(sqlConnection, sqlTransaction,
                        "UPDATE [workerdock].AlertEvaluationState SET LastEvaluatedAtUtc=@now,LastRuleId=@rule,LastSubjectKey=@subject WHERE StateId=1");
                    Add(updateCursor, "@now", SqlDbType.DateTime2, now);
                    Add(updateCursor, "@rule", SqlDbType.UniqueIdentifier, rule.Id);
                    Add(updateCursor, "@subject", SqlDbType.NVarChar, lastSubject is null ? DBNull.Value : lastSubject, 256);
                    await updateCursor.ExecuteNonQueryAsync(token);
                    processed++;
                    if (hasMoreSubjects) break;
                }
                if (rules.Count == 0)
                {
                    await using var reset = Command(sqlConnection, sqlTransaction,
                        "UPDATE [workerdock].AlertEvaluationState SET LastEvaluatedAtUtc=@now,LastRuleId=NULL,LastSubjectKey=NULL WHERE StateId=1");
                    Add(reset, "@now", SqlDbType.DateTime2, now);
                    await reset.ExecuteNonQueryAsync(token);
                }
                return processed;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask ReconcileRuleAsync(SqlConnection c, SqlTransaction t, Rule rule,
        Dictionary<string, DateTime> observations, DateTime now, CancellationToken ct)
    {
        var active = (await ReadActiveIncidentsAsync(c, t, rule.Id, ct))
            .ToDictionary(incident => IncidentKey(incident.Condition, incident.SubjectKey), StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var hasActive = active.Remove(IncidentKey(rule.Condition, observation.Key), out var incident);
            if (!hasActive && rule.Condition == "WorkerUnavailable")
                hasActive = active.Remove(IncidentKey("Unknown", observation.Key), out incident);
            if (hasActive && incident is not null)
            {
                if (incident.State == "Unknown")
                {
                    await UpdateIncidentAsync(c, t, incident.Id, "Open", rule.Condition, now, incident.NextReminderAtUtc, ct);
                    incident = incident with { State = "Open", Condition = rule.Condition, LastObservedAtUtc = now };
                }
                else
                    await UpdateIncidentAsync(c, t, incident.Id, "Open", rule.Condition, now, incident.NextReminderAtUtc, ct);

                if (incident.State == "Open" && rule.Enabled && incident.NextReminderAtUtc is DateTime reminder && reminder <= now && rule.ReminderSeconds is > 0)
                {
                    await EmitDeliveryAsync(c, t, incident.Id, rule.SinkId, "Reminder", now, ct);
                    var next = SafeAdd(now, rule.ReminderSeconds.Value);
                    await SetReminderAsync(c, t, incident.Id, next, ct);
                }
                continue;
            }

            var effectiveOpenAt = SafeAdd(observation.Value, rule.GraceSeconds);
            if (effectiveOpenAt > now || !rule.Enabled) continue;
            var incidentId = Guid.NewGuid();
            await using (var insert = Command(c, t, """
                INSERT [workerdock].AlertIncidents(IncidentId,RuleId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc,ResolvedAtUtc,NextReminderAtUtc)
                VALUES(@id,@rule,@subject,@condition,N'Open',@opened,@now,NULL,@next);
                """))
            {
                Add(insert, "@id", SqlDbType.UniqueIdentifier, incidentId);
                Add(insert, "@rule", SqlDbType.UniqueIdentifier, rule.Id);
                Add(insert, "@subject", SqlDbType.NVarChar, observation.Key, 256);
                Add(insert, "@condition", SqlDbType.NVarChar, rule.Condition, 16);
                Add(insert, "@opened", SqlDbType.DateTime2, effectiveOpenAt);
                Add(insert, "@now", SqlDbType.DateTime2, now);
                Add(insert, "@next", SqlDbType.DateTime2, rule.ReminderSeconds is > 0 ? SafeAdd(now, rule.ReminderSeconds.Value) : DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await EmitDeliveryAsync(c, t, incidentId, rule.SinkId, "Open", now, ct);
        }

        foreach (var incident in active.Values)
        {
            // A bounded TOP query is not proof that older keys recovered. Confirm the
            // specific active subject before resolving it, avoiding false recovery when
            // the number of matching subjects exceeds the evaluation batch size.
            if (rule.Enabled && await IsSubjectObservedAsync(c, t, rule, incident.SubjectKey, now, ct))
            {
                if (rule.Condition == "WorkerUnavailable" && incident.Condition == "Unknown")
                    await UpdateIncidentAsync(c, t, incident.Id, "Open", rule.Condition, now, incident.NextReminderAtUtc, ct);
                continue;
            }
            if (rule.Condition == "WorkerUnavailable" && !await WorkerTelemetryExistsAsync(c, t, incident.SubjectKey, ct))
            {
                await UpdateIncidentAsync(c, t, incident.Id, "Unknown", "Unknown", now, incident.NextReminderAtUtc, ct);
                continue;
            }

            await using var resolve = Command(c, t, """
                UPDATE [workerdock].AlertIncidents SET State=N'Resolved',Condition=@condition,
                    LastObservedAtUtc=@now,ResolvedAtUtc=@now,NextReminderAtUtc=NULL
                WHERE IncidentId=@id AND State IN(N'Open',N'Unknown');
                """);
            Add(resolve, "@condition", SqlDbType.NVarChar, rule.Condition, 16);
            Add(resolve, "@now", SqlDbType.DateTime2, now);
            Add(resolve, "@id", SqlDbType.UniqueIdentifier, incident.Id);
            if (await resolve.ExecuteNonQueryAsync(ct) == 1 && rule.Enabled)
                await EmitDeliveryAsync(c, t, incident.Id, rule.SinkId, "Resolve", now, ct);
        }
    }

    private async ValueTask<bool> IsSubjectObservedAsync(SqlConnection c, SqlTransaction t, Rule rule,
        string subject, DateTime now, CancellationToken ct)
    {
        if (!TrySubjectId(subject, rule.Condition switch
            {
                "LongRunning" => "execution:",
                "WorkerUnavailable" => "worker:",
                "MissedExecution" => "schedule:",
                _ => "job:"
            }, out var id)) return false;
        var from = now.AddSeconds(-rule.WindowSeconds);
        var sql = rule.Condition switch
        {
            "RepeatedFailure" => """
                SELECT CASE WHEN COUNT(DISTINCT e.ExecutionId)>=@threshold THEN 1 ELSE 0 END
                FROM [workerdock].Executions e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                WHERE e.JobId=@id AND e.State=N'Failed' AND a.State<>N'Running' AND a.EndedAtUtc>=@from AND a.EndedAtUtc<=@now;
                """,
            "RetryExhausted" => """
                SELECT CASE WHEN COUNT(DISTINCT e.ExecutionId)>=@threshold THEN 1 ELSE 0 END
                FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                CROSS APPLY(SELECT COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.MaxAttempts')),
                    TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.maxAttempts'))) MaxAttempts) p
                JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                WHERE e.JobId=@id AND e.State=N'Failed' AND p.MaxAttempts>0 AND a.AttemptNumber>=p.MaxAttempts
                  AND a.State<>N'Running' AND a.EndedAtUtc>=@from AND a.EndedAtUtc<=@now;
                """,
            "Timeout" => """
                SELECT CASE WHEN COUNT(DISTINCT e.ExecutionId)>=@threshold THEN 1 ELSE 0 END
                FROM [workerdock].Executions e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                WHERE e.JobId=@id AND e.State=N'TimedOut' AND a.EndedAtUtc>=@from AND a.EndedAtUtc<=@now;
                """,
            "LongRunning" => """
                SELECT CASE WHEN EXISTS(SELECT 1 FROM [workerdock].Executions e
                    WHERE e.ExecutionId=@id AND e.State=N'Running'
                      AND COALESCE((SELECT MAX(a.StartedAtUtc) FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId AND a.State=N'Running'),e.CreatedAtUtc)<=DATEADD(second,-@window,@now)) THEN 1 ELSE 0 END;
                """,
            "MissedExecution" => """
                SELECT CASE WHEN COALESCE(SUM(COALESCE(ExactMissedCount,SkippedOccurrences)),0)>=@threshold THEN 1 ELSE 0 END
                FROM [workerdock].ScheduleRecoveryEvents WHERE ScheduleId=@id AND CreatedAtUtc>=@from AND CreatedAtUtc<=@now;
                """,
            "WorkerUnavailable" => """
                SELECT CASE WHEN EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus
                    WHERE StateId=1 AND (Ready=0 OR LastHeartbeatUtc<=DATEADD(second,-@window,@now))) THEN 1 ELSE 0 END;
                """,
            _ => throw new WorkerDockException("AlertRuleUnavailable")
        };
        await using var command = Command(c, t, sql);
        Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        Add(command, "@threshold", SqlDbType.Int, rule.Threshold);
        Add(command, "@window", SqlDbType.Int, rule.WindowSeconds);
        Add(command, "@from", SqlDbType.DateTime2, from);
        Add(command, "@now", SqlDbType.DateTime2, now);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private async ValueTask<Dictionary<string, DateTime>> ReadObservationsAsync(SqlConnection c, SqlTransaction t,
        Rule rule, DateTime now, string? afterSubject, int limit, CancellationToken ct)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var from = now.AddSeconds(-rule.WindowSeconds);
        string sql = rule.Condition switch
        {
            "RepeatedFailure" => """
                SELECT TOP(@limit) N'job:'+CONVERT(nvarchar(36),e.JobId) SubjectKey,MAX(COALESCE(a.EndedAtUtc,e.CreatedAtUtc)) DetectedAtUtc
                FROM [workerdock].Executions e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                WHERE e.State=N'Failed' AND a.State<>N'Running' AND a.EndedAtUtc>=@from AND a.EndedAtUtc<=@now
                  AND (@after IS NULL OR N'job:'+CONVERT(nvarchar(36),e.JobId)>@after)
                GROUP BY e.JobId HAVING COUNT(DISTINCT e.ExecutionId)>=@threshold ORDER BY N'job:'+CONVERT(nvarchar(36),e.JobId);
                """,
            "RetryExhausted" => """
                SELECT TOP(@limit) N'job:'+CONVERT(nvarchar(36),e.JobId) SubjectKey,MAX(COALESCE(a.EndedAtUtc,e.CreatedAtUtc)) DetectedAtUtc
                FROM [workerdock].Executions e
                JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                CROSS APPLY(SELECT COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.MaxAttempts')),
                    TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.maxAttempts'))) MaxAttempts) p
                JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                WHERE e.State=N'Failed' AND p.MaxAttempts>0 AND a.AttemptNumber>=p.MaxAttempts AND a.State<>N'Running'
                  AND a.EndedAtUtc>=@from AND a.EndedAtUtc<=@now
                  AND (@after IS NULL OR N'job:'+CONVERT(nvarchar(36),e.JobId)>@after)
                GROUP BY e.JobId HAVING COUNT(DISTINCT e.ExecutionId)>=@threshold ORDER BY N'job:'+CONVERT(nvarchar(36),e.JobId);
                """,
            "Timeout" => """
                SELECT TOP(@limit) N'job:'+CONVERT(nvarchar(36),e.JobId) SubjectKey,MAX(COALESCE(a.EndedAtUtc,e.CreatedAtUtc)) DetectedAtUtc
                FROM [workerdock].Executions e JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId
                WHERE e.State=N'TimedOut' AND a.EndedAtUtc>=@from AND a.EndedAtUtc<=@now
                  AND (@after IS NULL OR N'job:'+CONVERT(nvarchar(36),e.JobId)>@after)
                GROUP BY e.JobId HAVING COUNT(DISTINCT e.ExecutionId)>=@threshold ORDER BY N'job:'+CONVERT(nvarchar(36),e.JobId);
                """,
            "LongRunning" => """
                SELECT TOP(@limit) N'execution:'+CONVERT(nvarchar(36),e.ExecutionId) SubjectKey,
                    DATEADD(second,@window,COALESCE(a.StartedAtUtc,e.CreatedAtUtc)) DetectedAtUtc
                FROM [workerdock].Executions e LEFT JOIN [workerdock].Attempts a ON a.ExecutionId=e.ExecutionId AND a.State=N'Running'
                WHERE e.State=N'Running' AND COALESCE(a.StartedAtUtc,e.CreatedAtUtc)<=DATEADD(second,-@window,@now)
                  AND (@after IS NULL OR N'execution:'+CONVERT(nvarchar(36),e.ExecutionId)>@after)
                ORDER BY N'execution:'+CONVERT(nvarchar(36),e.ExecutionId);
                """,
            "MissedExecution" => """
                SELECT TOP(@limit) N'schedule:'+CONVERT(nvarchar(36),r.ScheduleId) SubjectKey,MAX(r.CreatedAtUtc) DetectedAtUtc
                FROM [workerdock].ScheduleRecoveryEvents r
                WHERE r.CreatedAtUtc>=@from AND r.CreatedAtUtc<=@now
                  AND (@after IS NULL OR N'schedule:'+CONVERT(nvarchar(36),r.ScheduleId)>@after)
                GROUP BY r.ScheduleId HAVING SUM(COALESCE(r.ExactMissedCount,r.SkippedOccurrences))>=@threshold
                ORDER BY N'schedule:'+CONVERT(nvarchar(36),r.ScheduleId);
                """,
            "WorkerUnavailable" => """
                SELECT TOP(@limit) N'runtime' SubjectKey,
                    COALESCE(DATEADD(second,@window,LastHeartbeatUtc),@now) DetectedAtUtc
                FROM [workerdock].RuntimeStatus
                WHERE StateId=1 AND (Ready=0 OR LastHeartbeatUtc<=DATEADD(second,-@window,@now))
                  AND (@after IS NULL OR N'runtime'>@after);
                """,
            _ => throw new WorkerDockException("AlertRuleUnavailable")
        };

        await using var command = Command(c, t, sql);
        Add(command, "@limit", SqlDbType.Int, Math.Clamp(limit, 1, 1000));
        Add(command, "@threshold", SqlDbType.Int, rule.Threshold);
        Add(command, "@window", SqlDbType.Int, rule.WindowSeconds);
        Add(command, "@from", SqlDbType.DateTime2, from);
        Add(command, "@now", SqlDbType.DateTime2, now);
        Add(command, "@after", SqlDbType.NVarChar, afterSubject is null ? DBNull.Value : afterSubject, 256);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[BoundSubject(reader.GetString(0))] = reader.GetDateTime(1);
        return result;
    }

    private async ValueTask EmitDeliveryAsync(SqlConnection c, SqlTransaction t, Guid incidentId, Guid sinkId,
        string kind, DateTime now, CancellationToken ct)
    {
        Guid revisionId;
        int attemptBudget;
        await using (var select = Command(c, t, """
            SELECT r.SinkRevisionId,r.MaxAttempts FROM [workerdock].AlertSinks s
            JOIN [workerdock].AlertSinkRevisions r ON r.SinkId=s.SinkId AND r.Revision=s.CurrentRevision
            WHERE s.SinkId=@sink AND s.Enabled=1;
            """))
        {
            Add(select, "@sink", SqlDbType.UniqueIdentifier, sinkId);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return;
            revisionId = reader.GetGuid(0);
            attemptBudget = reader.GetInt32(1);
        }

        var deliveryId = Guid.NewGuid();
        await using (var delivery = Command(c, t, """
            INSERT [workerdock].AlertDeliveries(DeliveryId,IncidentId,SinkRevisionId,Kind,Status,AttemptOrdinal,AttemptCount,AttemptBudget,
                NextAvailableAtUtc,LeaseGeneration,CreatedAtUtc)
            VALUES(@id,@incident,@revision,@kind,N'Pending',1,0,@budget,@now,0,@now);
            """))
        {
            Add(delivery, "@id", SqlDbType.UniqueIdentifier, deliveryId);
            Add(delivery, "@incident", SqlDbType.UniqueIdentifier, incidentId);
            Add(delivery, "@revision", SqlDbType.UniqueIdentifier, revisionId);
            Add(delivery, "@kind", SqlDbType.NVarChar, kind, 16);
            Add(delivery, "@budget", SqlDbType.Int, attemptBudget);
            Add(delivery, "@now", SqlDbType.DateTime2, now);
            await delivery.ExecuteNonQueryAsync(ct);
        }

    }

    private static async ValueTask<IReadOnlyList<Rule>> ReadRulesAsync(SqlConnection c, SqlTransaction t, Guid? cursor,
        bool includeCursorRule, int limit, CancellationToken ct)
    {
        var rules = new List<Rule>(limit);
        if (includeCursorRule && cursor is Guid current)
            await ReadRuleRangeAsync(c, t, "WHERE RuleId=@cursor ORDER BY RuleId", current, 1, rules, ct);
        if (cursor is Guid id)
            await ReadRuleRangeAsync(c, t, "WHERE RuleId>@cursor ORDER BY RuleId", id, limit - rules.Count, rules, ct);
        if (rules.Count < limit)
            await ReadRuleRangeAsync(c, t, cursor is null ? "ORDER BY RuleId" : includeCursorRule ? "WHERE RuleId<@cursor ORDER BY RuleId" : "WHERE RuleId<=@cursor ORDER BY RuleId",
                cursor, limit - rules.Count, rules, ct);
        return rules;
    }

    private static async ValueTask ReadRuleRangeAsync(SqlConnection c, SqlTransaction t, string order, Guid? cursor,
        int limit, List<Rule> rules, CancellationToken ct)
    {
        if (limit <= 0) return;
        await using var command = Command(c, t, $"SELECT TOP(@limit) RuleId,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled FROM [workerdock].AlertRules {order}");
        Add(command, "@limit", SqlDbType.Int, limit);
        if (order.Contains("@cursor", StringComparison.Ordinal)) Add(command, "@cursor", SqlDbType.UniqueIdentifier, cursor!.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rules.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetGuid(6), reader.GetBoolean(7)));
    }

    private static async ValueTask<List<Incident>> ReadActiveIncidentsAsync(SqlConnection c, SqlTransaction t, Guid ruleId, CancellationToken ct)
    {
        var result = new List<Incident>();
        await using var command = Command(c, t, "SELECT IncidentId,SubjectKey,Condition,State,OpenedAtUtc,LastObservedAtUtc,NextReminderAtUtc FROM [workerdock].AlertIncidents WHERE RuleId=@rule AND State IN(N'Open',N'Unknown') ORDER BY IncidentId");
        Add(command, "@rule", SqlDbType.UniqueIdentifier, ruleId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4), reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        return result;
    }

    private static async ValueTask<bool> WorkerTelemetryExistsAsync(SqlConnection c, SqlTransaction t, string subject, CancellationToken ct)
    {
        if (subject != "runtime") return false;
        await using var command = Command(c, t, "SELECT CASE WHEN EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus WHERE StateId=1) THEN 1 ELSE 0 END");
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static async ValueTask UpdateIncidentAsync(SqlConnection c, SqlTransaction t, Guid id, string state,
        string condition, DateTime now, DateTime? reminder, CancellationToken ct)
    {
        await using var command = Command(c, t, "UPDATE [workerdock].AlertIncidents SET State=@state,Condition=@condition,LastObservedAtUtc=@now,NextReminderAtUtc=@reminder WHERE IncidentId=@id AND State IN(N'Open',N'Unknown')");
        Add(command, "@state", SqlDbType.NVarChar, state, 16);
        Add(command, "@condition", SqlDbType.NVarChar, condition, 16);
        Add(command, "@now", SqlDbType.DateTime2, now);
        Add(command, "@reminder", SqlDbType.DateTime2, reminder is DateTime value ? value : DBNull.Value);
        Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask SetReminderAsync(SqlConnection c, SqlTransaction t, Guid id, DateTime next, CancellationToken ct)
    {
        await using var command = Command(c, t, "UPDATE [workerdock].AlertIncidents SET NextReminderAtUtc=@next WHERE IncidentId=@id AND State=N'Open'");
        Add(command, "@next", SqlDbType.DateTime2, next);
        Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask<(DateTime? EvaluatedAtUtc, Guid? RuleId, string? SubjectKey)> ReadCursorAsync(SqlConnection c, SqlTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT LastEvaluatedAtUtc,LastRuleId,LastSubjectKey FROM [workerdock].AlertEvaluationState WITH(UPDLOCK,HOLDLOCK) WHERE StateId=1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("AlertEvaluationStateUnavailable");
        return (reader.IsDBNull(0) ? null : reader.GetDateTime(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private async ValueTask VerifyTenantAsync(SqlConnection c, SqlTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT InstanceId FROM [workerdock].TenantSettings");
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
    }

    private static async ValueTask<DateTime> ReadSqlNowAsync(SqlConnection c, SqlTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT SYSUTCDATETIME()");
        return DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync(ct))!, DateTimeKind.Utc);
    }

    private static DateTime SafeAdd(DateTime value, int seconds)
    {
        try { return value.AddSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return DateTime.MaxValue; }
    }

    private static string BoundSubject(string value) => value.Length <= 256 ? value : value[..256];
    private static string IncidentKey(string condition, string subject) => condition + "\0" + subject;
    private static bool TrySubjectId(string subject, string prefix, out Guid value)
    {
        value = Guid.Empty;
        return subject.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(subject.AsSpan(prefix.Length), out value);
    }

    private static SqlCommand Command(SqlConnection c, SqlTransaction t, string sql)
    { var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql; return command; }

    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    { var parameter = command.Parameters.Add(name, type); if (size != 0) parameter.Size = size; parameter.Value = value; }

    private sealed record Rule(Guid Id, string Condition, int Threshold, int WindowSeconds, int GraceSeconds,
        int? ReminderSeconds, Guid SinkId, bool Enabled);
    private sealed record Incident(Guid Id, string SubjectKey, string Condition, string State, DateTime OpenedAtUtc,
        DateTime LastObservedAtUtc, DateTime? NextReminderAtUtc);
}
