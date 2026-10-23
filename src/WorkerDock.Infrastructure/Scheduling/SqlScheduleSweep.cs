using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Infrastructure.Scheduling;

public sealed class SqlScheduleSweep(
    TenantDatabaseOptions options,
    IOccurrenceWriter occurrences,
    IScheduleRulesFingerprint? rulesFingerprint = null) : IScheduleSweep, IScheduleProjectionMaintenance
{
    private readonly IScheduleRulesFingerprint rules = rulesFingerprint ?? new SystemScheduleRulesFingerprint();

    public async ValueTask<int> RunDueAsync(DateTimeOffset throughUtc, int maxSchedules, CancellationToken ct)
    {
        ValidateBatch(maxSchedules);
        if (throughUtc.Offset != TimeSpan.Zero) throw new WorkerDockException("ValidationFailed", "throughUtc");

        try
        {
            await using var connection = new SqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            await CheckInstanceAsync(connection, null, ct);
            var schedules = new List<(Guid Id, long Revision)>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT TOP (@limit) s.ScheduleId,s.Revision FROM [workerdock].Schedules s JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.Removed=0 AND s.Paused=0 AND s.CursorUtc<@through AND j.Enabled=1 AND j.Archived=0 ORDER BY s.CursorUtc,s.ScheduleId";
                command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = maxSchedules });
                command.Parameters.Add(new SqlParameter("@through", SqlDbType.DateTime2) { Value = throughUtc.UtcDateTime });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) schedules.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            var created = 0;
            foreach (var schedule in schedules)
            {
                ct.ThrowIfCancellationRequested();
                created += await occurrences.MaterializeAsync(schedule.Id, schedule.Revision, throughUtc, ct);
            }
            return created;
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<int> RefreshZoneRulesAsync(int maxSchedules, CancellationToken ct)
    {
        ValidateBatch(maxSchedules);
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await CheckInstanceAsync(connection, transaction, token);
                Guid? lastScheduleId;
                await using (var state = Command(connection, transaction, "SELECT LastScheduleId FROM [workerdock].ScheduleRuleScanState WITH(UPDLOCK,HOLDLOCK) WHERE ScanId=1"))
                {
                    var value = await state.ExecuteScalarAsync(token);
                    lastScheduleId = value is null or DBNull ? null : (Guid)value;
                }

                var rows = new List<(Guid ScheduleId, Guid JobId, long Revision, string ZoneId, string Fingerprint, byte[] JobVersion)>();
                var sql = "SELECT TOP (@limit) s.ScheduleId,s.JobId,s.Revision,s.ZoneId,s.ZoneRulesFingerprint,j.Version FROM [workerdock].Schedules s WITH(UPDLOCK,READPAST,ROWLOCK) JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.Removed=0 AND (@last IS NULL OR s.ScheduleId>@last) ORDER BY s.ScheduleId";
                await using (var scan = Command(connection, transaction, sql))
                {
                    Add(scan, "@limit", SqlDbType.Int, maxSchedules);
                    Add(scan, "@last", SqlDbType.UniqueIdentifier, lastScheduleId ?? (object)DBNull.Value);
                    await using var reader = await scan.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<byte[]>(5)));
                }

                if (rows.Count == 0 && lastScheduleId is not null)
                {
                    await using var reset = Command(connection, transaction, "UPDATE [workerdock].ScheduleRuleScanState SET LastScheduleId=NULL WHERE ScanId=1");
                    await reset.ExecuteNonQueryAsync(token);
                    await using var scan = Command(connection, transaction, "SELECT TOP (@limit) s.ScheduleId,s.JobId,s.Revision,s.ZoneId,s.ZoneRulesFingerprint,j.Version FROM [workerdock].Schedules s WITH(UPDLOCK,READPAST,ROWLOCK) JOIN [workerdock].Jobs j ON j.JobId=s.JobId WHERE s.Removed=0 ORDER BY s.ScheduleId");
                    Add(scan, "@limit", SqlDbType.Int, maxSchedules);
                    await using var reader = await scan.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<byte[]>(5)));
                }

                var nextId = rows.Count == maxSchedules ? rows[^1].ScheduleId : (Guid?)null;
                await using (var advance = Command(connection, transaction, "UPDATE [workerdock].ScheduleRuleScanState SET LastScheduleId=@last WHERE ScanId=1"))
                {
                    Add(advance, "@last", SqlDbType.UniqueIdentifier, nextId ?? (object)DBNull.Value);
                    await advance.ExecuteNonQueryAsync(token);
                }

                var changed = 0;
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    var fingerprint = rules.GetFingerprint(row.ZoneId);
                    if (string.Equals(fingerprint, row.Fingerprint, StringComparison.Ordinal)) continue;
                    var revision = checked(row.Revision + 1);
                    await using (var update = Command(connection, transaction, "UPDATE [workerdock].Schedules SET Revision=@revision,ZoneRulesFingerprint=@fingerprint WHERE ScheduleId=@schedule AND Revision=@oldRevision AND Removed=0"))
                    {
                        Add(update, "@revision", SqlDbType.BigInt, revision);
                        Add(update, "@fingerprint", SqlDbType.Char, fingerprint, 64);
                        Add(update, "@schedule", SqlDbType.UniqueIdentifier, row.ScheduleId);
                        Add(update, "@oldRevision", SqlDbType.BigInt, row.Revision);
                        if (await update.ExecuteNonQueryAsync(token) != 1) continue;
                    }
                    await using (var bump = Command(connection, transaction, "UPDATE [workerdock].Jobs SET CurrentRevision=CurrentRevision WHERE JobId=@job AND Version=@version"))
                    {
                        Add(bump, "@job", SqlDbType.UniqueIdentifier, row.JobId);
                        Add(bump, "@version", SqlDbType.Binary, row.JobVersion, 8);
                        if (await bump.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
                    }
                    var actor = new ActorContext("system:timezone-rules", "System", row.ScheduleId);
                    await AuditAsync(connection, transaction, row.ScheduleId, row.Revision, revision, actor, token);
                    changed++;
                }
                return changed;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public ValueTask<int> ReconcileZoneRuleChangesAsync(CancellationToken ct) => RefreshZoneRulesAsync(100, ct);

    private static void ValidateBatch(int maxSchedules)
    {
        if (maxSchedules is < 1 or > 1000) throw new WorkerDockException("ValidationFailed", "maxSchedules");
    }

    private async ValueTask CheckInstanceAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
    }

    private static async ValueTask AuditAsync(DbConnection connection, DbTransaction transaction, Guid scheduleId, long before, long after, ActorContext actor, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(@id,N'ScheduleZoneRulesChanged',@subject,@mode,@correlation,SYSUTCDATETIME(),@details)");
        Add(command, "@id", SqlDbType.UniqueIdentifier, Guid.NewGuid());
        Add(command, "@subject", SqlDbType.NVarChar, actor.Subject, 300);
        Add(command, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
        Add(command, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
        Add(command, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(new { targetId = scheduleId, beforeRevision = before, afterRevision = after, outcome = "Applied" }), 4000);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = new SqlParameter(name, type) { Value = value };
        if (size != 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}