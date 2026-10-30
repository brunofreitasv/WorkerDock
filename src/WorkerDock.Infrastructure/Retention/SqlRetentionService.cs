using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Retention;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Retention;

public sealed class SqlRetentionService(TenantDatabaseOptions options, string? archiveRoot) : IRetentionService
{
    public async ValueTask<RetentionPolicyEditorDto> GetPolicyAsync(CancellationToken ct) => await RunAsync<RetentionPolicyEditorDto>(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction,
            "SELECT LogRetentionDays,ExecutionRetentionDays,AuditRetentionDays,ArchiveBeforeDelete,ArchiveRetentionDays,CONVERT(binary(8),RetentionVersion) FROM [workerdock].TenantSettings");
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new WorkerDockException("RetentionPolicyUnavailable");
        return new(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetInt32(4)), reader.GetFieldValue<byte[]>(5));
    }, ct);

    public async ValueTask<CommandReceipt> SavePolicyAsync(RetentionPolicy policy, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        try { RetentionPolicyValidation.Validate(policy); }
        catch (ArgumentException) { throw new WorkerDockException("ValidationFailed", "policy"); }
        if (expectedVersion is not { Length: 8 } || actor is null || string.IsNullOrWhiteSpace(actor.Subject) ||
            string.IsNullOrWhiteSpace(actor.AuthenticationMode) || actor.CorrelationId == Guid.Empty)
            throw new WorkerDockException("ValidationFailed");
        if (policy.ArchiveBeforeDelete && string.IsNullOrWhiteSpace(archiveRoot))
            throw new WorkerDockException("ArchiveUnavailable");

        return await RunAsync(async (connection, transaction, token) =>
        {
            await using var read = Command(connection, transaction, "SELECT LogRetentionDays,ExecutionRetentionDays,AuditRetentionDays,ArchiveBeforeDelete,ArchiveRetentionDays,CONVERT(binary(8),RetentionVersion) FROM [workerdock].TenantSettings WITH(UPDLOCK,HOLDLOCK)");
            await using var reader = await read.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new WorkerDockException("RetentionPolicyUnavailable");
            var before = new RetentionPolicy(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetInt32(4));
            var currentVersion = reader.GetFieldValue<byte[]>(5);
            await reader.CloseAsync();
            if (!currentVersion.AsSpan().SequenceEqual(expectedVersion)) throw new WorkerDockException("StaleVersion");
            await using var update = Command(connection, transaction, "UPDATE [workerdock].TenantSettings SET LogRetentionDays=@log,ExecutionRetentionDays=@execution,AuditRetentionDays=@audit,ArchiveBeforeDelete=@archive,ArchiveRetentionDays=@archiveDays,RetentionVersion=RetentionVersion+1 WHERE RetentionVersion=CONVERT(bigint,@version)");
            Add(update, "@log", SqlDbType.Int, policy.LogDays); Add(update, "@execution", SqlDbType.Int, policy.ExecutionDays);
            Add(update, "@audit", SqlDbType.Int, policy.AuditDays); Add(update, "@archive", SqlDbType.Bit, policy.ArchiveBeforeDelete);
            Add(update, "@archiveDays", SqlDbType.Int, policy.ArchiveDays); Add(update, "@version", SqlDbType.Binary, expectedVersion, 8);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
            await using var receiptCommand = Command(connection, transaction, "INSERT [workerdock].Commands(CommandId,TargetId,CommandKind,State,CreatedAtUtc) VALUES(@command,@target,N'RetentionPolicy',N'Applied',SYSUTCDATETIME()); INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(NEWID(),N'RetentionPolicyUpdated',@subject,@mode,@correlation,SYSUTCDATETIME(),@details)");
            var commandId = Guid.NewGuid();
            Add(receiptCommand, "@command", SqlDbType.UniqueIdentifier, commandId); Add(receiptCommand, "@target", SqlDbType.UniqueIdentifier, RetentionPolicy.TargetId);
            Add(receiptCommand, "@subject", SqlDbType.NVarChar, actor.Subject, 300); Add(receiptCommand, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
            Add(receiptCommand, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
            Add(receiptCommand, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(new { targetId = RetentionPolicy.TargetId, before, after = policy }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), 4000);
            await receiptCommand.ExecuteNonQueryAsync(token);
            return new CommandReceipt(commandId, RetentionPolicy.TargetId, "Applied");
        }, ct);
    }

    public ValueTask<int> RunBatchAsync(DateTimeOffset nowUtc, int maxRows, CancellationToken ct)
    {
        if (maxRows is < 1 or > 1000) throw new WorkerDockException("ValidationFailed");
        return RunBatchCoreAsync(nowUtc, maxRows, ct);
    }

    private async ValueTask<int> RunBatchCoreAsync(DateTimeOffset nowUtc, int maxRows, CancellationToken ct)
    {
        var policy = (await GetPolicyAsync(ct)).Policy;
        var logCutoff = nowUtc.UtcDateTime.AddDays(-policy.LogDays);
        var executionCutoff = nowUtc.UtcDateTime.AddDays(-policy.ExecutionDays);
        var auditCutoff = nowUtc.UtcDateTime.AddDays(-policy.AuditDays);
        Guid? batchId = null;
        var deleted = 0;

        if (policy.ArchiveBeforeDelete)
        {
            if (string.IsNullOrWhiteSpace(archiveRoot)) throw new WorkerDockException("ArchiveUnavailable");
            var batch = await GetPendingBatchAsync(ct) ?? await CreateArchiveBatchAsync(nowUtc, logCutoff, executionCutoff, auditCutoff, maxRows, ct);
            if (batch is null)
            {
                await PurgeExpiredTombstonesAsync(nowUtc.UtcDateTime, maxRows, ct);
                await ExpireArchivesAsync(nowUtc, policy.ArchiveDays, ct);
                return 0;
            }
            batchId = batch.BatchId;
            if (batch.State != "Finalized")
            {
                var manifest = await FinalizeArchiveBatchAsync(batch, ct);
                batch = batch with { Sha256 = manifest.Sha256, RowCount = manifest.RowCount, State = "Finalized", FinalizedUtc = manifest.FinalizedUtc.UtcDateTime };
            }
            var verified = new ArchiveManifest(batch.BatchId, batch.RelativePath, batch.Sha256, batch.RowCount,
                new DateTimeOffset(batch.FinalizedUtc ?? throw new WorkerDockException("ArchiveVerificationFailed"), TimeSpan.Zero));
            if (!await new ArchiveWriter(archiveRoot!).VerifyAsync(verified, ct)) throw new WorkerDockException("ArchiveVerificationFailed");
            logCutoff = batch.LogCutoffUtc;
            executionCutoff = batch.EligibleThroughUtc;
            auditCutoff = batch.EligibleThroughUtc;
        }

        var itemsDeleted = await PurgeOperationalEventsAsync(logCutoff, maxRows, batchId, ct);
        deleted += itemsDeleted;
        if (deleted < maxRows) deleted += await PurgeRetentionItemsAsync(logCutoff, executionCutoff, maxRows - deleted, batchId, ct);
        if (deleted < maxRows) deleted += await PurgeAuditEventsAsync(auditCutoff, maxRows - deleted, batchId, ct);
        if (deleted < maxRows) deleted += await PurgeExpiredTombstonesAsync(nowUtc.UtcDateTime, maxRows - deleted, ct);
        await ExpireArchivesAsync(nowUtc, policy.ArchiveDays, ct);
        return deleted;
    }

    private async ValueTask<ArchiveBatch?> GetPendingBatchAsync(CancellationToken ct) => await RunAsync(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, """
            SELECT TOP(1) b.BatchId,b.RelativePath,b.Sha256,b.[RowCount],b.EligibleThroughUtc,b.LogCutoffUtc,b.State,b.FinalizedAtUtc,
                b.PurgedRowCount
            FROM [workerdock].ArchiveBatches b
            WHERE b.State IN(N'Writing',N'Failed') OR (b.State=N'Finalized' AND b.[RowCount]>b.PurgedRowCount)
            ORDER BY CASE WHEN b.State IN(N'Writing',N'Failed') THEN 0 ELSE 1 END,b.CreatedAtUtc,b.BatchId;
            """);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new ArchiveBatch(reader.GetGuid(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetInt64(3), reader.GetDateTime(4), Utc(reader.GetDateTime(5)), reader.GetString(6),
                reader.IsDBNull(7) ? null : Utc(reader.GetDateTime(7)), reader.GetInt64(8))
            : null;
    }, ct);

    private async ValueTask<ArchiveBatch?> CreateArchiveBatchAsync(DateTimeOffset nowUtc, DateTime logCutoff, DateTime executionCutoff, DateTime auditCutoff, int maxRows, CancellationToken ct)
    {
        try
        {
            return await RunAsync(async (connection, transaction, token) =>
        {
            var id = Guid.NewGuid();
            var path = id.ToString("N") + ".archive.gz";
            var through = nowUtc.UtcDateTime;
            await using (var insert = Command(connection, transaction, "INSERT [workerdock].ArchiveBatches(BatchId,RelativePath,Sha256,[RowCount],EligibleThroughUtc,LogCutoffUtc,State,CreatedAtUtc) VALUES(@id,@path,@sha,0,@through,@logCutoff,N'Writing',SYSUTCDATETIME())"))
            {
                Add(insert, "@id", SqlDbType.UniqueIdentifier, id); Add(insert, "@path", SqlDbType.NVarChar, path, 500);
                Add(insert, "@sha", SqlDbType.Char, new string('0', 64), 64); Add(insert, "@through", SqlDbType.DateTime2, through);
                Add(insert, "@logCutoff", SqlDbType.DateTime2, logCutoff);
                await insert.ExecuteNonQueryAsync(token);
            }
            await using (var stage = Command(connection, transaction, """
                DECLARE @EligibleExecutions TABLE(ExecutionId uniqueidentifier NOT NULL PRIMARY KEY);
                INSERT @EligibleExecutions(ExecutionId)
                SELECT TOP(@max) e.ExecutionId
                FROM [workerdock].Executions e
                WHERE e.CreatedAtUtc<@executionCutoff AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped')
                  AND e.AggregatedAtUtc IS NOT NULL
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId AND a.State=N'Running')
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].OperationalEvents v WHERE v.EventKind=N'Execution' AND v.ExecutionId=e.ExecutionId AND v.AtUtc>@logCutoff)
                  AND e.HoldsJobSlot=0
                  AND (e.ManualRequestExpiresAtUtc IS NULL OR e.ManualRequestExpiresAtUtc<SYSUTCDATETIME())
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].RetentionRequestTombstones d WHERE d.ExecutionId=e.ExecutionId AND d.ExpiresAtUtc>=SYSUTCDATETIME())
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),e.ExecutionId) AND i.State IN(N'Open',N'Unknown'))
                ORDER BY e.CreatedAtUtc,e.ExecutionId;

                INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId)
                SELECT @id,N'AuditEvent',a.EventId FROM (SELECT TOP(@max) EventId FROM [workerdock].AuditEvents WHERE OccurredAtUtc<@auditCutoff ORDER BY OccurredAtUtc,EventId) a;
                INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId)
                SELECT @id,N'OperationalEvent',v.EventId FROM [workerdock].OperationalEvents v
                WHERE v.EventKind=N'Execution' AND v.AtUtc<=@logCutoff
                  AND EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=v.ExecutionId AND e.AggregatedAtUtc IS NOT NULL AND e.State IN(N'Succeeded',N'Failed',N'Stopped',N'TimedOut',N'Skipped'))
                  AND NOT EXISTS(SELECT 1 FROM @EligibleExecutions e WHERE e.ExecutionId=v.ExecutionId)
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].Attempts a WHERE a.ExecutionId=v.ExecutionId AND a.State=N'Running')
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].Executions e WHERE e.ExecutionId=v.ExecutionId AND e.HoldsJobSlot=1)
                  AND NOT EXISTS(SELECT 1 FROM [workerdock].AlertIncidents i WHERE i.SubjectKey=N'execution:'+CONVERT(nvarchar(36),v.ExecutionId) AND i.State IN(N'Open',N'Unknown'));

                INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId)
                SELECT @id,N'Execution',ExecutionId FROM @EligibleExecutions;
                INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId)
                SELECT @id,N'ExecutionAttempt',a.AttemptId FROM [workerdock].Attempts a JOIN @EligibleExecutions e ON e.ExecutionId=a.ExecutionId;
                INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId)
                SELECT @id,N'OperationalEvent',v.EventId FROM [workerdock].OperationalEvents v JOIN @EligibleExecutions e ON e.ExecutionId=v.ExecutionId
                WHERE v.EventKind=N'Execution' AND v.AtUtc<=@logCutoff
                AND NOT EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@id AND i.RecordKind=N'OperationalEvent' AND i.RecordId=v.EventId);
                INSERT [workerdock].ArchiveBatchItems(BatchId,RecordKind,RecordId)
                SELECT @id,N'OperationalEvent',v.EventId FROM [workerdock].OperationalEvents v
                WHERE v.EventKind=N'ScheduleRecovery' AND v.AtUtc<=@logCutoff;
                UPDATE [workerdock].ArchiveBatches SET [RowCount]=(SELECT COUNT_BIG(*) FROM [workerdock].ArchiveBatchItems WHERE BatchId=@id) WHERE BatchId=@id;
                """))
            {
                Add(stage, "@id", SqlDbType.UniqueIdentifier, id); Add(stage, "@max", SqlDbType.Int, maxRows);
                Add(stage, "@logCutoff", SqlDbType.DateTime2, logCutoff); Add(stage, "@executionCutoff", SqlDbType.DateTime2, executionCutoff);
                Add(stage, "@auditCutoff", SqlDbType.DateTime2, auditCutoff);
                await stage.ExecuteNonQueryAsync(token);
            }
            await using var count = Command(connection, transaction, "SELECT [RowCount],RelativePath,Sha256,EligibleThroughUtc,LogCutoffUtc,State FROM [workerdock].ArchiveBatches WHERE BatchId=@id");
            Add(count, "@id", SqlDbType.UniqueIdentifier, id);
            await using var reader = await count.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new WorkerDockException("RetentionUnavailable");
            var rowCount = reader.GetInt64(0);
            if (rowCount == 0)
                throw new EmptyArchiveBatchException();
            return new ArchiveBatch(id, reader.GetString(1), reader.GetString(2).Trim(), rowCount, reader.GetDateTime(3), Utc(reader.GetDateTime(4)), reader.GetString(5), null, 0);
            }, ct);
        }
        catch (EmptyArchiveBatchException) { return null; }
    }

    private async ValueTask<ArchiveManifest> FinalizeArchiveBatchAsync(ArchiveBatch batch, CancellationToken ct)
    {
        try
        {
            var writer = new ArchiveWriter(archiveRoot!);
            await using var records = await ReadArchiveRecordsAsync(batch.BatchId, ct);
            var manifest = await writer.WriteAsync(batch.BatchId, records, ct);
            if (manifest.RowCount != batch.RowCount || !await writer.VerifyAsync(manifest, ct))
                throw new WorkerDockException("ArchiveVerificationFailed");
            await RunAsync(async (connection, transaction, token) =>
            {
                await using var finalize = Command(connection, transaction, "UPDATE [workerdock].ArchiveBatches SET Sha256=@sha,[RowCount]=@rows,State=N'Finalized',FinalizedAtUtc=@finalized WHERE BatchId=@id AND State IN(N'Writing',N'Failed') AND [RowCount]=@rows");
                Add(finalize, "@sha", SqlDbType.Char, manifest.Sha256, 64); Add(finalize, "@rows", SqlDbType.BigInt, manifest.RowCount);
                Add(finalize, "@finalized", SqlDbType.DateTime2, manifest.FinalizedUtc.UtcDateTime);
                Add(finalize, "@id", SqlDbType.UniqueIdentifier, batch.BatchId);
                if (await finalize.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("ArchiveVerificationFailed");
                return true;
            }, ct);
            return manifest;
        }
        catch
        {
            await MarkArchiveBatchFailedAsync(batch.BatchId, ct);
            throw;
        }
    }

    private async ValueTask<MemoryStream> ReadArchiveRecordsAsync(Guid batchId, CancellationToken ct) => await RunAsync(async (connection, transaction, token) =>
    {
        await using var command = Command(connection, transaction, """
            SELECT i.RecordKind,i.RecordId,
            CASE i.RecordKind
              WHEN N'AuditEvent' THEN (SELECT x.EventId,x.EventType,x.ActorSubject,x.AuthenticationMode,x.CorrelationId,x.OccurredAtUtc,JSON_QUERY(x.DetailsJson) Details FROM [workerdock].AuditEvents x WHERE x.EventId=i.RecordId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
              WHEN N'Execution' THEN (SELECT x.ExecutionId,x.JobId,x.JobRevision,x.ProfileRevisionId,x.TriggerKind,x.ScheduleId,x.ScheduleRevision,x.ScheduledForUtc,x.State,x.Certainty,x.AvailableAtUtc,x.CorrelationId,x.CreatedAtUtc,x.StopRequested,x.ForceStop,x.ValueContextJson,x.FirstStartedAtUtc,x.ValueSnapshotJson,x.ValueResolutionError,x.RequestKey,x.ManualActorScopeHash,x.ManualRequestKeyHash,x.ManualRequestHash,x.ManualCommandId,x.ManualResponseState,x.ManualRequestExpiresAtUtc FROM [workerdock].Executions x WHERE x.ExecutionId=i.RecordId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
              WHEN N'ExecutionAttempt' THEN (SELECT x.AttemptId,x.ExecutionId,x.AttemptNumber,x.WorkerBootId,x.Generation,x.State,x.LeaseExpiresUtc,x.StartedAtUtc,x.DeadlineUtc,x.EndedAtUtc,x.SafeCode,x.LocalExitConfirmed,x.EffectsMayHaveOccurred,JSON_QUERY((SELECT d.DiagnosticJson FROM [workerdock].AttemptFailureDiagnostics d WHERE d.AttemptId=x.AttemptId)) FailureDiagnostic FROM [workerdock].Attempts x WHERE x.AttemptId=i.RecordId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
              WHEN N'OperationalEvent' THEN (SELECT x.EventId,x.EventKind,x.ExecutionId,x.AttemptId,x.Source,x.Sequence,x.AtUtc,x.Code,JSON_QUERY(x.SafeFieldsJson) SafeFields,x.ScheduleId,x.ScheduleRevision,x.FromUtc,x.ThroughUtc,x.Policy,x.CreatedExecutions,x.ExactMissedCount,x.SkippedOccurrences FROM [workerdock].OperationalEvents x WHERE x.EventId=i.RecordId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
            END PayloadJson
            FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=@id ORDER BY i.RecordKind,i.RecordId;
            """);
        Add(command, "@id", SqlDbType.UniqueIdentifier, batchId);
        var stream = new MemoryStream();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            using var jsonWriter = new Utf8JsonWriter(stream);
            while (await reader.ReadAsync(token))
            {
                if (reader.IsDBNull(2)) throw new InvalidDataException("An archive source record is missing.");
                var payload = reader.GetString(2);
                using var document = JsonDocument.Parse(payload);
                jsonWriter.WriteStartObject(); jsonWriter.WriteString("kind", reader.GetString(0)); jsonWriter.WriteString("id", reader.GetGuid(1));
                jsonWriter.WritePropertyName("record");
                if (reader.GetString(0) == "OperationalEvent") WriteOperationalEventRecord(jsonWriter, document.RootElement);
                else document.RootElement.WriteTo(jsonWriter);
                jsonWriter.WriteEndObject(); jsonWriter.Flush();
                stream.WriteByte((byte)'\n');
                if (stream.Length > 268_435_456) throw new InvalidDataException("Archive batch exceeds the configured size limit.");
            }
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }, ct);

    private static void WriteOperationalEventRecord(Utf8JsonWriter writer, JsonElement record)
    {
        writer.WriteStartObject();
        foreach (var property in record.EnumerateObject())
        {
            if (property.NameEquals("SafeFields"))
            {
                writer.WritePropertyName(property.Name);
                WriteSafeOperationalFields(writer, property.Value);
            }
            else property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteSafeOperationalFields(Utf8JsonWriter writer, JsonElement fields)
    {
        writer.WriteStartObject();
        if (fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in fields.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.String) continue;
                var value = field.Value.GetString();
                if (value is null || !IsSafeOperationalField(field.Name, value)) continue;
                writer.WriteString(field.Name, value);
            }
        }
        writer.WriteEndObject();
    }

    private static bool IsSafeOperationalField(string name, string value) => name switch
    {
        "executionId" or "attemptId" or "correlationId" => Guid.TryParseExact(value, "D", out _),
        "durationMs" or "affectedRows" => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0,
        "outcome" => value is "Success" or "TransientFailure" or "PermanentFailure",
        "safeCode" => value is "Success" or "SecretUnavailable" or "TemplateCancelled" or "TemplateExecutionFailed" or "TemplateConfigurationInvalid" or "ConnectivityFailed" or "AuthenticationFailed" or "DestinationUnavailable" or "CommandFailed" or "Timeout",
        _ => false
    };

    private async ValueTask MarkArchiveBatchFailedAsync(Guid batchId, CancellationToken ct)
    {
        try
        {
            await RunAsync(async (connection, transaction, token) =>
            {
                await using var command = Command(connection, transaction, "UPDATE [workerdock].ArchiveBatches SET State=N'Failed' WHERE BatchId=@id AND State=N'Writing'");
                Add(command, "@id", SqlDbType.UniqueIdentifier, batchId); await command.ExecuteNonQueryAsync(token); return true;
            }, ct);
        }
        catch { }
    }

    private ValueTask<int> PurgeRetentionItemsAsync(DateTime cutoffLogs, DateTime cutoffExecutions, int maxRows, Guid? batchId, CancellationToken ct) =>
        RunProcedureAsync("[workerdock].PurgeRetentionItems", new Dictionary<string, object?>
        {
            ["@LogCutoffUtc"] = cutoffLogs, ["@ExecutionCutoffUtc"] = cutoffExecutions, ["@MaxRows"] = maxRows, ["@BatchId"] = batchId
        }, ct);

    private ValueTask<int> PurgeOperationalEventsAsync(DateTime cutoffLogs, int maxRows, Guid? batchId, CancellationToken ct) =>
        RunProcedureAsync("[workerdock].PurgeOperationalEvents", new Dictionary<string, object?>
        {
            ["@LogCutoffUtc"] = cutoffLogs, ["@MaxRows"] = maxRows, ["@BatchId"] = batchId
        }, ct);

    private ValueTask<int> PurgeAuditEventsAsync(DateTime cutoff, int maxRows, Guid? batchId, CancellationToken ct) =>
        RunProcedureAsync("[workerdock].PurgeAuditEvents", new Dictionary<string, object?>
        {
            ["@CutoffUtc"] = cutoff, ["@MaxRows"] = maxRows, ["@ArchiveBatchId"] = batchId
        }, ct);

    private ValueTask<int> PurgeExpiredTombstonesAsync(DateTime cutoff, int maxRows, CancellationToken ct) =>
        RunProcedureAsync("[workerdock].PurgeRetentionRequestTombstones", new Dictionary<string, object?>
        {
            ["@CutoffUtc"] = cutoff, ["@MaxRows"] = maxRows
        }, ct);

    private async ValueTask ExpireArchivesAsync(DateTimeOffset nowUtc, int archiveDays, CancellationToken ct)
    {
        if (archiveDays < 1) return;
        var finalizedBeforeUtc = nowUtc.UtcDateTime.AddDays(-archiveDays);
        var expired = await RunAsync(async (connection, transaction, token) =>
        {
            var batches = new List<Guid>();
            await using var command = Command(connection, transaction, """
                SELECT TOP(100) b.BatchId
                FROM [workerdock].ArchiveBatches b
                WHERE b.State=N'Finalized' AND b.FinalizedAtUtc<@finalizedBefore
                  AND b.[RowCount]<=b.PurgedRowCount
                  AND (b.ArchiveRemovedAtUtc IS NULL
                    OR EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=b.BatchId)
                    OR b.PurgedRowCount>0)
                ORDER BY b.FinalizedAtUtc,b.BatchId;
                """);
            Add(command, "@finalizedBefore", SqlDbType.DateTime2, finalizedBeforeUtc);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) batches.Add(reader.GetGuid(0));
            return batches;
        }, ct);

        if (expired.Count == 0) return;
        foreach (var batchId in expired) await ExpireArchiveBatchAsync(batchId, nowUtc, ct);
    }

    private async ValueTask ExpireArchiveBatchAsync(Guid batchId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await RunAsync(async (connection, transaction, token) =>
        {
            await using var policyCommand = Command(connection, transaction, "SELECT ArchiveRetentionDays,AuditRetentionDays FROM [workerdock].TenantSettings WITH(UPDLOCK,HOLDLOCK)");
            await using var policyReader = await policyCommand.ExecuteReaderAsync(token);
            if (!await policyReader.ReadAsync(token)) throw new WorkerDockException("RetentionPolicyUnavailable");
            var archiveDays = policyReader.GetInt32(0);
            var auditDays = policyReader.GetInt32(1);
            await policyReader.CloseAsync();
            if (archiveDays < 1) return 0;

            var finalizedBeforeUtc = nowUtc.UtcDateTime.AddDays(-archiveDays);
            var auditCutoffUtc = nowUtc.UtcDateTime.AddDays(-auditDays);
            string relativePath;
            bool archiveRemoved;
            await using (var batchCommand = Command(connection, transaction, """
            SELECT b.RelativePath,CONVERT(bit,CASE WHEN b.ArchiveRemovedAtUtc IS NULL THEN 0 ELSE 1 END)
            FROM [workerdock].ArchiveBatches b WITH(UPDLOCK,HOLDLOCK)
            WHERE b.BatchId=@id AND b.State=N'Finalized' AND b.FinalizedAtUtc<@finalizedBefore
              AND b.[RowCount]<=b.PurgedRowCount
              AND (b.ArchiveRemovedAtUtc IS NULL
                OR EXISTS(SELECT 1 FROM [workerdock].ArchiveBatchItems i WHERE i.BatchId=b.BatchId)
                OR b.PurgedRowCount>0);
            """))
            {
                Add(batchCommand, "@id", SqlDbType.UniqueIdentifier, batchId);
                Add(batchCommand, "@finalizedBefore", SqlDbType.DateTime2, finalizedBeforeUtc);
                await using var batchReader = await batchCommand.ExecuteReaderAsync(token);
                if (!await batchReader.ReadAsync(token)) return 0;
                relativePath = batchReader.GetString(0);
                archiveRemoved = batchReader.GetBoolean(1);
            }

            if (!archiveRemoved)
            {
                if (string.IsNullOrWhiteSpace(archiveRoot)) throw new WorkerDockException("ArchiveUnavailable");
                await new ArchiveWriter(archiveRoot).DeleteAsync(relativePath, token);
            }

            await using var purge = Command(connection, transaction, "DECLARE @purged int; EXEC [workerdock].PurgeExpiredArchiveBatchItems @BatchId=@id,@FinalizedBeforeUtc=@finalizedBefore,@AuditCutoffUtc=@auditCutoff,@MaxRows=@max,@PurgedRows=@purged OUTPUT; SELECT @purged;");
            Add(purge, "@id", SqlDbType.UniqueIdentifier, batchId); Add(purge, "@finalizedBefore", SqlDbType.DateTime2, finalizedBeforeUtc);
            Add(purge, "@auditCutoff", SqlDbType.DateTime2, auditCutoffUtc); Add(purge, "@max", SqlDbType.Int, 1000);
            var purged = Convert.ToInt32(await purge.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
            if (purged > 0) await RecordPurgeAsync(connection, transaction, "ArchiveMetadata", finalizedBeforeUtc, purged, batchId, token);
            return 0;
        }, ct);
    }

    private async ValueTask<int> RunProcedureAsync(string name, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct) =>
        await RunAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, $"DECLARE @purged int; EXEC {name} " +
                string.Join(",", parameters.Keys.Select(key => key + "=" + key)) + ",@PurgedRows=@purged OUTPUT; SELECT @purged;");
            foreach (var parameter in parameters) Add(command, parameter.Key, SqlDbType.Variant, parameter.Value);
            var purged = Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
            if (purged > 0)
            {
                var kind = name switch
                {
                    "[workerdock].PurgeOperationalEvents" => "OperationalEvents",
                    "[workerdock].PurgeRetentionItems" => "RetentionItems",
                    "[workerdock].PurgeAuditEvents" => "AuditEvents",
                    "[workerdock].PurgeRetentionRequestTombstones" => "RequestTombstones",
                    _ => "RetentionItems"
                };
                var cutoff = parameters.TryGetValue("@CutoffUtc", out var auditCutoff) ? auditCutoff as DateTime?
                    : parameters.TryGetValue("@LogCutoffUtc", out var logCutoff) ? logCutoff as DateTime?
                    : parameters.TryGetValue("@ExecutionCutoffUtc", out var executionCutoff) ? executionCutoff as DateTime?
                    : null;
                var batch = parameters.TryGetValue("@ArchiveBatchId", out var archiveBatch) ? archiveBatch as Guid?
                    : parameters.TryGetValue("@BatchId", out var batchId) ? batchId as Guid? : null;
                if (batch is Guid coverageBatch && kind is ("AuditEvents" or "RetentionItems" or "OperationalEvents"))
                    await RecordArchivePurgeProgressAsync(connection, transaction, coverageBatch, purged, token);
                await RecordPurgeAsync(connection, transaction, kind, cutoff, purged, batch, token);
            }
            return purged;
        }, ct);

    private static async ValueTask RecordPurgeAsync(DbConnection connection, DbTransaction transaction, string kind,
        DateTime? cutoff, int purged, Guid? batchId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "INSERT [workerdock].AuditEvents(EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES(NEWID(),N'RetentionPurge',N'WorkerDock.Retention',N'System',NEWID(),SYSUTCDATETIME(),@details)");
        Add(command, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(new { purgeKind = kind, cutoffUtc = cutoff, purgedRowCount = purged, archiveBatchId = batchId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), 4000);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask RecordArchivePurgeProgressAsync(DbConnection connection, DbTransaction transaction,
        Guid batchId, int purged, CancellationToken ct)
    {
        if (purged <= 0) return;
        await using var command = Command(connection, transaction, """
            EXEC sys.sp_set_session_context @key=N'WDArchivePurgeBatchId',@value=@batch;
            BEGIN TRY
                UPDATE [workerdock].ArchiveBatches SET PurgedRowCount=PurgedRowCount+@purged WHERE BatchId=@batch AND State=N'Finalized' AND PurgedRowCount+@purged<=[RowCount];
                DECLARE @updated int=@@ROWCOUNT;
                EXEC sys.sp_set_session_context @key=N'WDArchivePurgeBatchId',@value=NULL;
                SELECT @updated;
            END TRY
            BEGIN CATCH
                EXEC sys.sp_set_session_context @key=N'WDArchivePurgeBatchId',@value=NULL;
                THROW;
            END CATCH;
            """);
        Add(command, "@batch", SqlDbType.UniqueIdentifier, batchId);
        Add(command, "@purged", SqlDbType.BigInt, (long)purged);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1)
            throw new WorkerDockException("ArchivePurgeCoverageMismatch");
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

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static void Add(DbCommand command, string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name;
        parameter.DbType = type switch
        {
            SqlDbType.Binary => DbType.Binary, SqlDbType.Int => DbType.Int32, SqlDbType.BigInt => DbType.Int64,
            SqlDbType.Bit => DbType.Boolean, SqlDbType.UniqueIdentifier => DbType.Guid,
            SqlDbType.NVarChar => DbType.String, SqlDbType.Char => DbType.AnsiStringFixedLength,
            SqlDbType.DateTime2 => DbType.DateTime2, _ => DbType.Object
        };
        if (size != 0) parameter.Size = size;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record ArchiveBatch(Guid BatchId, string RelativePath, string Sha256, long RowCount, DateTime EligibleThroughUtc, DateTime LogCutoffUtc, string State, DateTime? FinalizedUtc, long PurgedRows);
    private sealed class EmptyArchiveBatchException : Exception { }
}
