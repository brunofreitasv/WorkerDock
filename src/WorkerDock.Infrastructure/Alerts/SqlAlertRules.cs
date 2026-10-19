using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Alerts;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class SqlAlertRules(TenantDatabaseOptions options) : IAlertRules, IAlertSinks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<IReadOnlyList<AlertRuleEditorDto>> ListAsync(CancellationToken ct) => RunAsync<IReadOnlyList<AlertRuleEditorDto>>(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, "SELECT TOP(200) RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,Version FROM [workerdock].AlertRules ORDER BY Name,RuleId");
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<AlertRuleEditorDto>();
        while (await reader.ReadAsync(token)) result.Add(ReadRule(reader));
        return result;
    }, ct);

    public ValueTask<AlertRuleEditorDto> GetAsync(Guid id, CancellationToken ct) => RunAsync(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, "SELECT RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,Version FROM [workerdock].AlertRules WHERE RuleId=@id");
        Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new WorkerDockException("AlertRuleNotFound");
        return ReadRule(reader);
    }, ct);

    public async ValueTask<CommandReceipt> SaveAsync(AlertRule rule, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        try { AlertRuleValidation.Validate(rule); JobCommandValidation.ValidateActor(actor); }
        catch (ArgumentException) { throw new WorkerDockException("ValidationFailed", "rule"); }
        if (expectedVersion is { Length: not 8 }) throw new WorkerDockException("ValidationFailed", "version");

        return await RunAsync(async (connection, transaction, token) =>
        {
            var current = await ReadRuleRowAsync(connection, transaction, rule.Id, token);
            if (current is null && expectedVersion is not null || current is not null && (expectedVersion is null || !current.Value.Version.AsSpan().SequenceEqual(expectedVersion)))
                throw new WorkerDockException("StaleVersion");
            await EnsureEnabledSinkAsync(connection, transaction, rule.SinkId, token);
            var revision = checked((current?.Revision ?? 0) + 1);
            if (current is null)
            {
                await using var insert = Command(connection, transaction, "INSERT [workerdock].AlertRules(RuleId,Name,Condition,Threshold,WindowSeconds,GraceSeconds,ReminderSeconds,SinkId,Enabled,CurrentRevision) VALUES(@id,@name,@condition,@threshold,@window,@grace,@reminder,@sink,@enabled,@revision)");
                BindRule(insert, rule, revision);
                await insert.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var update = Command(connection, transaction, "UPDATE [workerdock].AlertRules SET Name=@name,Condition=@condition,Threshold=@threshold,WindowSeconds=@window,GraceSeconds=@grace,ReminderSeconds=@reminder,SinkId=@sink,Enabled=@enabled,CurrentRevision=@revision WHERE RuleId=@id AND Version=@version");
                BindRule(update, rule, revision);
                Add(update, "@version", SqlDbType.Binary, expectedVersion!, 8);
                if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
            }
            var snapshot = JsonSerializer.Serialize(rule, JsonOptions);
            await using (var history = Command(connection, transaction, "INSERT [workerdock].AlertRuleRevisions(RuleRevisionId,RuleId,Revision,SnapshotJson,CreatedAtUtc) VALUES(@revisionId,@id,@revision,@snapshot,SYSUTCDATETIME())"))
            {
                Add(history, "@revisionId", SqlDbType.UniqueIdentifier, Guid.NewGuid()); Add(history, "@id", SqlDbType.UniqueIdentifier, rule.Id);
                Add(history, "@revision", SqlDbType.BigInt, revision); Add(history, "@snapshot", SqlDbType.NVarChar, snapshot, 4000);
                await history.ExecuteNonQueryAsync(token);
            }
            return await SqlConnectionProfiles.ReceiptAsync(connection, transaction, rule.Id, current is null ? "AlertRuleCreated" : "AlertRuleUpdated", actor, token,
                beforeRevision: current?.Revision, afterRevision: revision);
        }, ct);
    }

    ValueTask<IReadOnlyList<AlertSinkEditorDto>> IAlertSinks.ListAsync(CancellationToken ct) => RunAsync<IReadOnlyList<AlertSinkEditorDto>>(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, "SELECT TOP(200) s.SinkId,s.Name,s.Enabled,s.Version,r.SinkType,r.ConfigurationJson,r.MaxAttempts,r.RetryMinSeconds,r.RetryMaxSeconds,r.TimeoutSeconds FROM [workerdock].AlertSinks s JOIN [workerdock].AlertSinkRevisions r ON r.SinkId=s.SinkId AND r.Revision=s.CurrentRevision ORDER BY s.Name,s.SinkId");
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<AlertSinkEditorDto>();
        while (await reader.ReadAsync(token)) result.Add(ReadSink(reader));
        return result;
    }, ct);

    ValueTask<AlertSinkEditorDto> IAlertSinks.GetAsync(Guid id, CancellationToken ct) => RunAsync(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, "SELECT s.SinkId,s.Name,s.Enabled,s.Version,r.SinkType,r.ConfigurationJson,r.MaxAttempts,r.RetryMinSeconds,r.RetryMaxSeconds,r.TimeoutSeconds FROM [workerdock].AlertSinks s JOIN [workerdock].AlertSinkRevisions r ON r.SinkId=s.SinkId AND r.Revision=s.CurrentRevision WHERE s.SinkId=@id");
        Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new WorkerDockException("AlertSinkNotFound");
        return ReadSink(reader);
    }, ct);

    async ValueTask<CommandReceipt> IAlertSinks.SaveAsync(AlertSinkDefinition sink, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        sink = AlertSinkValidation.ValidateAndFreeze(sink);
        try { JobCommandValidation.ValidateActor(actor); }
        catch (ArgumentException) { throw new WorkerDockException("ValidationFailed", "actor"); }
        if (expectedVersion is { Length: not 8 }) throw new WorkerDockException("ValidationFailed", "version");
        var references = ReadSecretReferences(sink.Configuration);

        return await RunAsync(async (connection, transaction, token) =>
        {
            var current = await ReadSinkRowAsync(connection, transaction, sink.Id, token);
            if (current is null && expectedVersion is not null || current is not null && (expectedVersion is null || !current.Value.Version.AsSpan().SequenceEqual(expectedVersion)))
                throw new WorkerDockException("StaleVersion");
            var revision = checked((current?.Revision ?? 0) + 1);
            if (current is null)
            {
                await using var insert = Command(connection, transaction, "INSERT [workerdock].AlertSinks(SinkId,Name,CurrentRevision,Enabled) VALUES(@id,@name,@revision,@enabled)");
                Add(insert, "@id", SqlDbType.UniqueIdentifier, sink.Id); Add(insert, "@name", SqlDbType.NVarChar, sink.Name, 150);
                Add(insert, "@revision", SqlDbType.BigInt, revision); Add(insert, "@enabled", SqlDbType.Bit, sink.Enabled);
                await insert.ExecuteNonQueryAsync(token);
            }
            else
            {
                await using var update = Command(connection, transaction, "UPDATE [workerdock].AlertSinks SET Name=@name,CurrentRevision=@revision,Enabled=@enabled WHERE SinkId=@id AND Version=@version");
                Add(update, "@id", SqlDbType.UniqueIdentifier, sink.Id); Add(update, "@name", SqlDbType.NVarChar, sink.Name, 150);
                Add(update, "@revision", SqlDbType.BigInt, revision); Add(update, "@enabled", SqlDbType.Bit, sink.Enabled); Add(update, "@version", SqlDbType.Binary, expectedVersion!, 8);
                if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
            }
            var configuration = sink.Configuration.GetRawText();
            await using (var history = Command(connection, transaction, "INSERT [workerdock].AlertSinkRevisions(SinkRevisionId,SinkId,Revision,SinkType,ConfigurationJson,SecretReferencesJson,MaxAttempts,RetryMinSeconds,RetryMaxSeconds,TimeoutSeconds,CreatedAtUtc) VALUES(@revisionId,@id,@revision,@type,@configuration,@references,@attempts,@retryMin,@retryMax,@timeout,SYSUTCDATETIME())"))
            {
                Add(history, "@revisionId", SqlDbType.UniqueIdentifier, Guid.NewGuid()); Add(history, "@id", SqlDbType.UniqueIdentifier, sink.Id);
                Add(history, "@revision", SqlDbType.BigInt, revision); Add(history, "@type", SqlDbType.NVarChar, sink.Type, 80); Add(history, "@configuration", SqlDbType.NVarChar, configuration, 4000);
                Add(history, "@references", SqlDbType.NVarChar, JsonSerializer.Serialize(references, JsonOptions), 2000);
                Add(history, "@attempts", SqlDbType.Int, sink.MaxAttempts); Add(history, "@retryMin", SqlDbType.Int, (int)sink.RetryBaseDelay.TotalSeconds);
                Add(history, "@retryMax", SqlDbType.Int, (int)sink.RetryMaxDelay.TotalSeconds); Add(history, "@timeout", SqlDbType.Int, (int)sink.Timeout.TotalSeconds);
                await history.ExecuteNonQueryAsync(token);
            }
            return await SqlConnectionProfiles.ReceiptAsync(connection, transaction, sink.Id, current is null ? "AlertSinkCreated" : "AlertSinkUpdated", actor, token,
                beforeRevision: current?.Revision, afterRevision: revision);
        }, ct);
    }

    private async ValueTask<T> RunAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await using var identity = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                return await action(connection, transaction, token);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static async ValueTask<(long Revision, byte[] Version)?> ReadRuleRowAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT CurrentRevision,Version FROM [workerdock].AlertRules WHERE RuleId=@id"); Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)) : null;
    }

    private static async ValueTask<(long Revision, byte[] Version)?> ReadSinkRowAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT CurrentRevision,Version FROM [workerdock].AlertSinks WHERE SinkId=@id"); Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)) : null;
    }

    private static async ValueTask EnsureEnabledSinkAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT Enabled FROM [workerdock].AlertSinks WHERE SinkId=@id"); Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new WorkerDockException("AlertSinkUnavailable", "sinkId");
    }

    private static AlertRuleEditorDto ReadRule(DbDataReader reader) => new(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
        TimeSpan.FromSeconds(reader.GetInt32(4)), TimeSpan.FromSeconds(reader.GetInt32(5)), TimeSpan.FromSeconds(reader.IsDBNull(6) ? 0 : reader.GetInt32(6)),
        reader.GetGuid(7), reader.GetBoolean(8)), reader.GetFieldValue<byte[]>(9));

    private static AlertSinkEditorDto ReadSink(DbDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(5));
        var definition = new AlertSinkDefinition(reader.GetGuid(0), reader.GetString(1), reader.GetString(4), document.RootElement.Clone(), reader.GetInt32(6),
            TimeSpan.FromSeconds(reader.GetInt32(7)), TimeSpan.FromSeconds(reader.GetInt32(8)), TimeSpan.FromSeconds(reader.GetInt32(9)), reader.GetBoolean(2));
        return new(definition, reader.GetFieldValue<byte[]>(3));
    }

    private static IReadOnlyList<string> ReadSecretReferences(JsonElement configuration)
    {
        if (!configuration.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array) return [];
        return headers.EnumerateArray().Where(header => header.TryGetProperty("secretReference", out _))
            .Select(header => header.GetProperty("secretReference").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void BindRule(DbCommand command, AlertRule rule, long revision)
    {
        Add(command, "@id", SqlDbType.UniqueIdentifier, rule.Id); Add(command, "@name", SqlDbType.NVarChar, rule.Name, 150);
        Add(command, "@condition", SqlDbType.NVarChar, rule.Condition, 16); Add(command, "@threshold", SqlDbType.Int, rule.Threshold);
        Add(command, "@window", SqlDbType.Int, checked((int)rule.Window.TotalSeconds)); Add(command, "@grace", SqlDbType.Int, checked((int)rule.Grace.TotalSeconds));
        Add(command, "@reminder", SqlDbType.Int, rule.ReminderInterval == TimeSpan.Zero ? null : checked((int)rule.ReminderInterval.TotalSeconds));
        Add(command, "@sink", SqlDbType.UniqueIdentifier, rule.SinkId); Add(command, "@enabled", SqlDbType.Bit, rule.Enabled); Add(command, "@revision", SqlDbType.BigInt, revision);
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }

    private static void Add(DbCommand command, string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.DbType = type switch
        {
            SqlDbType.UniqueIdentifier => DbType.Guid, SqlDbType.NVarChar => DbType.String, SqlDbType.Binary => DbType.Binary,
            SqlDbType.BigInt => DbType.Int64, SqlDbType.Int => DbType.Int32, SqlDbType.Bit => DbType.Boolean, _ => DbType.Object
        };
        if (size != 0) parameter.Size = size; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
