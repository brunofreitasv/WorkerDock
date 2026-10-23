using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Application.Settings;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Settings;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.Infrastructure.Executions;

public sealed class SqlAttemptStore(TenantDatabaseOptions options, ExecutionValuePreparation? values = null) : IAttemptStore, IAttemptSignals
{
    private readonly ExecutionValuePreparation preparation = values ?? new ExecutionValuePreparation(new DynamicValueResolver());
    public ValueTask<AttemptStart> BeginAsync(DispatchIntent intent, string workerBootId, CancellationToken ct) =>
        TransactAsync(intent.ExecutionId, workerBootId, (data, now) => data.Begin(intent, workerBootId, now), ct);
    public async ValueTask<AttemptStart> BeginAsync(Guid executionId, string workerBootId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerBootId) || workerBootId.Length > 100) throw new WorkerDockException("ValidationFailed", "workerBootId");
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyAsync(connection, transaction, token);
                var data = await LoadAsync(connection, transaction, executionId, workerBootId, token, direct: true);
                var previous = data.Lease;
                var previousState = data.AttemptState;
                var now = await NowAsync(connection, transaction, token);
                if (!data.GateOpen || !data.DescriptorAvailable || data.StopRequested ||
                    data.State is not ("Queued" or "RetryWaiting") || data.Intent.DueUtc > now)
                    return new AttemptStart(data.State == "NeedsReview" ? "NeedsReview" : "Held", null);
                if (!await CanClaimAsync(connection, transaction, data, executionId, token))
                    return new AttemptStart("Held", null);
                var result = data.Begin(data.Intent, workerBootId, now);
                await SaveAsync(connection, transaction, data, previous, previousState,
                    previousState == "Running" && previous!.ExpiresUtc <= now, token);
                return result;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    public async ValueTask<ExecutionDescriptor> GetDescriptorAsync(AttemptLease lease, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyAsync(connection, transaction, token);
                var data = await LoadAsync(connection, transaction, lease.ExecutionId, lease.WorkerBootId, token);
                var previous = data.Lease; var previousState = data.AttemptState;
                var now = await NowAsync(connection, transaction, token);
                data.GetDescriptor(lease, now);
                data.Prepare(preparation);
                var snapshot = await SqlIntegrationSettings.ReadSnapshotAsync(connection, transaction, token);
                data.Descriptor = data.Descriptor with { IntegrationConfiguration = IntegrationConfigurationJson.Serialize(snapshot) };
                var descriptor = data.GetDescriptor(lease, await NowAsync(connection, transaction, token));
                if (descriptor.ValueSnapshot is not null && !ExecutionPayloadLimits.FitsPreparedDescriptor(descriptor))
                {
                    data.Descriptor = data.Descriptor with { ValueSnapshot = null, ValueResolutionError = "DynamicSnapshotTooLarge",
                        Configuration = data.OriginalConfiguration };
                    descriptor = data.GetDescriptor(lease, await NowAsync(connection, transaction, token));
                }
                await SaveAsync(connection, transaction, data, previous, previousState, previousState == "Running" && previous!.ExpiresUtc <= now, token);
                return descriptor;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    public async ValueTask<AttemptLease?> RenewAsync(AttemptLease lease, CancellationToken ct)
    {
        try { return await TransactAsync(lease.ExecutionId, null, (data, now) => data.Renew(lease, now), ct); }
        catch (WorkerDockException e) when (e.Code == "AttemptOwnershipLost") { return null; }
    }
    public async ValueTask CompleteAsync(AttemptLease lease, AttemptCompletion completion, CancellationToken ct)
    {
        try { await TransactAsync(lease.ExecutionId, null, (data, now) => { data.Complete(lease, completion, now); return true; }, ct); }
        catch (WorkerDockException e) when (e.Code == "AttemptOwnershipLost") { }
    }
    public ValueTask<AttemptSignal> ReadSignalAsync(AttemptLease lease, CancellationToken ct) =>
        TransactAsync(lease.ExecutionId, null, (data, now) => new AttemptSignal(data.Owned(lease, now), data.StopRequested, data.Force), ct);

    public async ValueTask<int> ReviewExpiredAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyAsync(connection, transaction, token);
                await using var command = Command(connection, transaction,
                    "SELECT TOP (@limit) ExecutionId FROM [workerdock].Attempts WHERE State=N'Running' AND LeaseExpiresUtc<=SYSUTCDATETIME() ORDER BY LeaseExpiresUtc,AttemptId");
                Add(command, "@limit", SqlDbType.Int, limit);
                var ids = new List<Guid>();
                await using (var reader = await command.ExecuteReaderAsync(token))
                    while (await reader.ReadAsync(token)) ids.Add(reader.GetGuid(0));
                var count = 0;
                foreach (var id in ids.Distinct())
                {
                    var data = await LoadAsync(connection, transaction, id, null, token);
                    var previous = data.Lease; var previousState = data.AttemptState;
                    var now = await NowAsync(connection, transaction, token);
                    if (!data.Review(now)) continue;
                    await SaveAsync(connection, transaction, data, previous, previousState, true, token); count++;
                }
                return count;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    internal async ValueTask<T> TransactAsync<T>(Guid id, string? boot, Func<AttemptLifecycle, DateTimeOffset, T> action,
        CancellationToken ct, Func<DbConnection, DbTransaction, T, CancellationToken, ValueTask>? after = null)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (connection, transaction, token) =>
            {
                await VerifyAsync(connection, transaction, token);
                var data = await LoadAsync(connection, transaction, id, boot, token);
                var previous = data.Lease; var previousState = data.AttemptState;
                var now = await NowAsync(connection, transaction, token);
                var result = action(data, now);
                await SaveAsync(connection, transaction, data, previous, previousState,
                    previousState == "Running" && previous!.ExpiresUtc <= now, token);
                if (after is not null) await after(connection, transaction, result, token);
                return result;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async ValueTask VerifyAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT InstanceId FROM [workerdock].TenantSettings");
        if ((Guid?)await command.ExecuteScalarAsync(ct) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
    }
    private static async ValueTask<DateTimeOffset> NowAsync(DbConnection c, DbTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT SYSUTCDATETIME()");
        return Utc((DateTime)(await command.ExecuteScalarAsync(ct))!);
    }
    private static async ValueTask<AttemptLifecycle> LoadAsync(DbConnection c, DbTransaction t, Guid id, string? boot, CancellationToken ct, bool direct = false)
    {
        await using var command = Command(c, t, """
            SELECT e.JobId,e.JobRevision,e.IdempotencyKey,e.State,e.Certainty,e.AvailableAtUtc,e.ProfileRevisionId,
                e.StopRequested,e.ForceStop,r.TemplateId,r.TemplateVersion,r.ConfigurationJson,r.PolicyJson,
                j.Enabled,j.Archived,cat.Enabled,CONVERT(bit,0),CONVERT(uniqueidentifier,NULL),CONVERT(nvarchar(max),NULL),CONVERT(char(64),NULL),
                CASE WHEN @direct=1 AND cat.Enabled=1 AND cat.ConcurrencyLimit>0 THEN 1
                    WHEN @direct=0 AND EXISTS(SELECT 1 FROM [workerdock].RuntimeStatus runtime
                        WHERE runtime.StateId=1 AND runtime.Ready=1 AND runtime.LastHeartbeatUtc>DATEADD(second,-30,SYSUTCDATETIME())
                        AND runtime.HostCeiling>(SELECT COUNT(*) FROM [workerdock].Attempts active WHERE active.State=N'Running')
                        AND e.HoldsJobSlot=1) THEN 1 ELSE 0 END,CONVERT(bigint,0),e.TriggerKind,e.ValueContextJson,
                e.FirstStartedAtUtc,e.ValueSnapshotJson,e.ValueResolutionError,r.DynamicValuesJson
            FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
                JOIN [workerdock].Jobs j ON j.JobId=e.JobId
                LEFT JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
            WHERE e.ExecutionId=@id
            """);
        Add(command, "@id", SqlDbType.UniqueIdentifier, id); Add(command, "@boot", SqlDbType.NVarChar, boot, 100);
        Add(command, "@direct", SqlDbType.Bit, direct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("ExecutionNotFound");
        var policy = JsonSerializer.Deserialize<ExecutionPolicy>(reader.GetString(12)) ?? throw new WorkerDockException("PinnedDescriptorUnavailable");
        ExecutionValueContext? context = null;
        var contextInvalid = false;
        if (!reader.IsDBNull(23))
        {
            try { context = JsonSerializer.Deserialize<ExecutionValueContext>(reader.GetString(23)); }
            catch (JsonException) { contextInvalid = true; }
            if (context is null) contextInvalid = true;
        }
        if (context is not null) context = context with { FirstStartedAtUtc = reader.IsDBNull(24) ? null : Utc(reader.GetDateTime(24)) };
        DynamicValueSnapshot? valueSnapshot = null;
        var snapshotInvalid = false;
        if (!reader.IsDBNull(25))
        {
            try { valueSnapshot = JsonSerializer.Deserialize<DynamicValueSnapshot>(reader.GetString(25)); }
            catch (JsonException) { snapshotInvalid = true; }
            if (valueSnapshot is null) snapshotInvalid = true;
        }
        DynamicValuePlan? dynamicPlan = null;
        string? planError = null;
        if (!reader.IsDBNull(27))
        {
            try { using var planJson = JsonDocument.Parse(reader.GetString(27)); dynamicPlan = DynamicValueJson.Read(planJson.RootElement); }
            catch (Exception error) when (error is JsonException or ValueResolutionException) { planError = "DynamicBindingInvalid"; }
        }
        var resolutionError = reader.IsDBNull(26) ? null : reader.GetString(26);
        if (snapshotInvalid || contextInvalid) resolutionError = "DynamicSnapshotInvalid";
        if (contextInvalid) valueSnapshot = null;
        if (context is null && dynamicPlan?.Bindings.Count > 0 && resolutionError is null)
            resolutionError = "DynamicSourceUnavailable";
        var descriptor = new ExecutionDescriptor(id, 1, reader.GetString(9), reader.GetString(10), reader.GetString(2),
            JsonDocument.Parse(reader.GetString(11)).RootElement.Clone(), reader.IsDBNull(6) ? null : reader.GetGuid(6), null,
            JsonSerializer.SerializeToElement(new { sqlProfileDiagnostic=reader.GetString(22)=="Diagnostic" }), null,
            context, valueSnapshot, resolutionError);
        var capacityRevision = reader.GetInt64(21);
        DispatchIntent intent;
        intent = new DispatchIntent(Guid.NewGuid(), id, 1, descriptor.TemplateId, Utc(reader.GetDateTime(5)), policy.Priority,
            reader.GetInt64(1), "", capacityRevision);
        var jobId=reader.GetGuid(0);var state=reader.GetString(3);
        var uncertain=reader.GetString(4)=="Unknown";var stop=reader.GetBoolean(7);var force=reader.GetBoolean(8);
        var gateOpen=reader.GetBoolean(13) && !reader.GetBoolean(14) && !reader.GetBoolean(16) && reader.GetInt32(20)==1;
        var capacity=reader.GetInt64(21);var available=!reader.IsDBNull(15) && reader.GetBoolean(15);
        var storedFirstStart = reader.IsDBNull(24) ? (DateTimeOffset?)null : Utc(reader.GetDateTime(24));
        var storedSnapshotPresent = !reader.IsDBNull(25);
        var storedResolutionError = reader.IsDBNull(26) ? null : reader.GetString(26);
        await reader.CloseAsync();
        if(descriptor.ProfileRevisionId is {} profileRevision)
        {
            await using var profile=Command(c,t,"SELECT ConfigurationJson FROM [workerdock].ConnectionProfileRevisions WHERE ProfileRevisionId=@revision");
            Add(profile,"@revision",SqlDbType.UniqueIdentifier,profileRevision);
            if(await profile.ExecuteScalarAsync(ct) is string profileJson)
                descriptor=descriptor with { ProfileConfiguration=JsonDocument.Parse(profileJson).RootElement.Clone() };
            else available=false;
        }
        var data = new AttemptLifecycle
        {
            EffectiveCapacityRevision=capacity,Descriptor=descriptor,OriginalConfiguration=descriptor.Configuration.Clone(),
            DynamicValues=dynamicPlan,DynamicPlanError=planError,
            StoredFirstStartedAtUtc=storedFirstStart,
            StoredSnapshot=valueSnapshot,StoredSnapshotPresent=storedSnapshotPresent,StoredResolutionError=storedResolutionError,
            Policy=policy,JobId=jobId,
            Intent=intent with { PayloadDigest=intent.ComputeDigest() },State=state,Uncertain=uncertain,
            StopRequested=stop,Force=force,GateOpen=gateOpen,DescriptorAvailable=available
        };
        await using var attempt = Command(c, t, "SELECT TOP(1) AttemptId,AttemptNumber,Generation,WorkerBootId,LeaseExpiresUtc,DeadlineUtc,State,LocalExitConfirmed,SafeCode FROM [workerdock].Attempts WHERE ExecutionId=@id ORDER BY AttemptNumber DESC");
        Add(attempt, "@id", SqlDbType.UniqueIdentifier, id);
        await using var ar = await attempt.ExecuteReaderAsync(ct);
        if (await ar.ReadAsync(ct))
        {
            data.Lease = new(ar.GetGuid(0), id, ar.GetInt32(1), ar.GetInt64(2), ar.IsDBNull(3) ? "" : ar.GetString(3),
                ar.IsDBNull(4) ? DateTimeOffset.MinValue : Utc(ar.GetDateTime(4)), ar.IsDBNull(5) ? null : Utc(ar.GetDateTime(5)));
            data.AttemptState = ar.GetString(6); data.LocalExitConfirmed = ar.GetBoolean(7); data.SafeCode = ar.IsDBNull(8) ? null : ar.GetString(8);
        }
        if (direct)
        {
            var next = intent with { DeliveryId = Guid.NewGuid(), AttemptOrdinal = (data.Lease?.Ordinal ?? 0) + 1,
                DueUtc = intent.DueUtc, Priority = policy.Priority, CapacityRevision = capacityRevision, PayloadDigest = "" };
            data.Intent = next with { PayloadDigest = next.ComputeDigest() };
        }
        return data;
    }

    private static async ValueTask<bool> CanClaimAsync(DbConnection c, DbTransaction t, AttemptLifecycle data, Guid executionId, CancellationToken ct)
    {
        await using var limits = Command(c, t, """
            SELECT j.ConcurrencyLimit,cat.ConcurrencyLimit,e.State
            FROM [workerdock].Executions e JOIN [workerdock].Jobs j ON j.JobId=e.JobId
            JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            JOIN [workerdock].TemplateCatalog cat ON cat.TemplateId=r.TemplateId AND cat.TemplateVersion=r.TemplateVersion
            WHERE e.ExecutionId=@id AND j.Enabled=1 AND j.Archived=0
            """);
        Add(limits, "@id", SqlDbType.UniqueIdentifier, executionId);
        await using var lr = await limits.ExecuteReaderAsync(ct);
        if (!await lr.ReadAsync(ct)) return false;
        var jobLimit = lr.GetInt32(0);
        var templateLimit = lr.GetInt32(1);
        var state = lr.GetString(2);
        await lr.CloseAsync();

        // RetryWaiting retains its durable admission slot until completion.
        var holdsSlot = state == "RetryWaiting";
        await using var admission = Command(c, t, """
            SELECT e.ExecutionId,e.State,COALESCE(JSON_VALUE(r.PolicyJson,'$.Overlap'),N'Queue'),
                COALESCE(TRY_CONVERT(int,JSON_VALUE(r.PolicyJson,'$.BacklogLimit')),100)
            FROM [workerdock].Executions e JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
              WHERE e.JobId=@job AND e.State IN(N'Queued',N'Running',N'RetryWaiting',N'NeedsReview')
                  AND (e.State<>N'Queued' OR e.StopRequested=0)
            ORDER BY e.AdmissionSequence,e.ExecutionId
            """);
        Add(admission, "@job", SqlDbType.UniqueIdentifier, data.JobId);
        var candidates = new List<AdmissionCandidate>();
        await using (var reader = await admission.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var candidateState = reader.GetString(1);
                candidates.Add(new(reader.GetGuid(0), reader.GetString(2), reader.GetInt32(3),
                    candidateState is "Running" or "RetryWaiting" or "NeedsReview"));
            }
        }
        if (holdsSlot) return templateLimit > 0;
        var decision = JobAdmission.Decide(candidates, jobLimit);
        if (decision.SkipReasons.TryGetValue(executionId, out var reason))
        {
            await using var skip = Command(c, t, """
                UPDATE [workerdock].Executions SET State=N'Skipped' WHERE ExecutionId=@id AND State=N'Queued';
                IF @@ROWCOUNT=1 INSERT INTO [workerdock].OperationalEvents(EventId,EventKind,ExecutionId,Source,Sequence,AtUtc,Code,SafeFieldsJson)
                    VALUES(NEWID(),N'Execution',@id,N'Lifecycle',0,SYSUTCDATETIME(),@reason,N'{}');
                UPDATE [workerdock].Executions SET HoldsJobSlot=0 WHERE ExecutionId=@id;
                """);
            Add(skip, "@id", SqlDbType.UniqueIdentifier, executionId);
            Add(skip, "@reason", SqlDbType.NVarChar, reason, 40);
            await skip.ExecuteNonQueryAsync(ct);
            await SqlCommandOutcomes.SettleAsync(c, t, executionId, ct);
            return false;
        }
        if (!decision.Admit.Contains(executionId)) return false;
        await using var template = Command(c, t, """
            SELECT COUNT(*) FROM [workerdock].Executions e
            JOIN [workerdock].JobRevisions r ON r.JobId=e.JobId AND r.Revision=e.JobRevision
            WHERE r.TemplateId=@template AND e.State IN(N'Running',N'RetryWaiting',N'NeedsReview')
            """);
        Add(template, "@template", SqlDbType.NVarChar, data.Descriptor.TemplateId, 100);
        return Convert.ToInt32(await template.ExecuteScalarAsync(ct)) < templateLimit;
    }

    private static async ValueTask SaveAsync(DbConnection c, DbTransaction t, AttemptLifecycle data,
        AttemptLease? previous, string? previousState, bool expired, CancellationToken ct)
    {
        if (data.Lease != previous || data.AttemptState != previousState)
        {
            var lease = data.Lease!;
            var insert = previous is null || previous.AttemptId != lease.AttemptId;
            await using var command = Command(c, t, insert ? """
                INSERT INTO [workerdock].Attempts(AttemptId,ExecutionId,AttemptNumber,WorkerBootId,Generation,State,LeaseExpiresUtc,StartedAtUtc,DeadlineUtc,LocalExitConfirmed,EffectsMayHaveOccurred)
                VALUES(@attempt,@execution,@ordinal,@boot,@generation,@state,@expires,SYSUTCDATETIME(),@deadline,@exit,@effects)
                """ : """
                UPDATE [workerdock].Attempts SET State=@state,LeaseExpiresUtc=@expires,LocalExitConfirmed=@exit,EffectsMayHaveOccurred=@effects,
                    EndedAtUtc=CASE WHEN @state=N'Running' THEN NULL ELSE SYSUTCDATETIME() END,SafeCode=@code
                WHERE AttemptId=@attempt AND ExecutionId=@execution AND Generation=@generation AND WorkerBootId=@boot AND State=@previousState
                    AND (@previousState<>N'Running' OR (@expired=1 AND LeaseExpiresUtc<=SYSUTCDATETIME()) OR (@expired=0 AND LeaseExpiresUtc>SYSUTCDATETIME()))
                """);
            Add(command, "@attempt", SqlDbType.UniqueIdentifier, lease.AttemptId); Add(command, "@execution", SqlDbType.UniqueIdentifier, lease.ExecutionId);
            Add(command, "@ordinal", SqlDbType.Int, lease.Ordinal); Add(command, "@boot", SqlDbType.NVarChar, lease.WorkerBootId, 100);
            Add(command, "@generation", SqlDbType.BigInt, lease.Generation); Add(command, "@state", SqlDbType.NVarChar, data.AttemptState, 30);
            Add(command, "@expires", SqlDbType.DateTime2, lease.ExpiresUtc.UtcDateTime); Add(command, "@deadline", SqlDbType.DateTime2, lease.DeadlineUtc?.UtcDateTime);
            Add(command, "@exit", SqlDbType.Bit, data.LocalExitConfirmed); Add(command, "@effects", SqlDbType.Bit, data.Uncertain);
            Add(command, "@previousState", SqlDbType.NVarChar, previousState, 30); Add(command, "@expired", SqlDbType.Bit, expired);
            Add(command, "@code", SqlDbType.NVarChar, data.SafeCode, 100);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new WorkerDockException("AttemptOwnershipLost");
        }
        var firstStart = data.Descriptor.ValueContext?.FirstStartedAtUtc;
        var newStart = data.StoredFirstStartedAtUtc is null && firstStart is not null;
        var newSnapshot = !data.StoredSnapshotPresent && data.Descriptor.ValueSnapshot is not null;
        var newError = data.StoredResolutionError is null && !data.StoredSnapshotPresent && data.Descriptor.ValueResolutionError is not null;
        if (newStart || newSnapshot || newError)
        {
            await using var values = Command(c, t, """
                UPDATE [workerdock].Executions
                SET FirstStartedAtUtc=COALESCE(FirstStartedAtUtc,@first),
                    ValueSnapshotJson=COALESCE(ValueSnapshotJson,@snapshot),
                    ValueResolutionError=COALESCE(ValueResolutionError,@error)
                WHERE ExecutionId=@id AND (@first IS NULL OR FirstStartedAtUtc IS NULL OR FirstStartedAtUtc=@first)
                    AND (@snapshot IS NULL OR (ValueSnapshotJson IS NULL AND ValueResolutionError IS NULL))
                    AND (@error IS NULL OR (ValueSnapshotJson IS NULL AND ValueResolutionError IS NULL))
                """);
            Add(values, "@id", SqlDbType.UniqueIdentifier, data.Descriptor.ExecutionId);
            Add(values, "@first", SqlDbType.DateTime2, newStart ? firstStart!.Value.UtcDateTime : null);
            Add(values, "@snapshot", SqlDbType.NVarChar, newSnapshot ? JsonSerializer.Serialize(data.Descriptor.ValueSnapshot) : null, -1);
            Add(values, "@error", SqlDbType.NVarChar, newError ? data.Descriptor.ValueResolutionError : null, 80);
            if (await values.ExecuteNonQueryAsync(ct) != 1) throw new WorkerDockException("AttemptOwnershipLost");
        }
        if (data.Events.Count == 0)
        {
            if (data.State is "Queued" or "RetryWaiting" && !data.GateOpen)
            {
            }
            return;
        }
        await using var update = Command(c, t, "UPDATE [workerdock].Executions SET State=@state,Certainty=@certainty,AvailableAtUtc=@due,StopRequested=@stop,ForceStop=@force,HoldsJobSlot=CASE WHEN @release=1 THEN 0 WHEN @state IN(N'Running',N'RetryWaiting',N'NeedsReview') THEN 1 ELSE HoldsJobSlot END WHERE ExecutionId=@id;");
        Add(update, "@id", SqlDbType.UniqueIdentifier, data.Descriptor.ExecutionId); Add(update, "@state", SqlDbType.NVarChar, data.State, 30);
        Add(update, "@certainty", SqlDbType.NVarChar, data.Uncertain ? "Unknown" : "Known", 10); Add(update, "@due", SqlDbType.DateTime2, data.Intent.DueUtc.UtcDateTime);
        Add(update, "@stop", SqlDbType.Bit, data.StopRequested); Add(update, "@force", SqlDbType.Bit, data.Force); Add(update, "@release", SqlDbType.Bit, !data.HoldsJobSlot);
        await update.ExecuteNonQueryAsync(ct);
        await SqlCommandOutcomes.SettleAsync(c, t, data.Descriptor.ExecutionId, ct);
        foreach (var state in data.Events)
        {
            await using var ev = Command(c, t, "INSERT INTO [workerdock].OperationalEvents(EventId,EventKind,ExecutionId,AttemptId,Source,Sequence,AtUtc,Code,SafeFieldsJson) VALUES(NEWID(),N'Execution',@id,@attempt,N'Lifecycle',0,SYSUTCDATETIME(),@state,N'{}')");
            Add(ev, "@id", SqlDbType.UniqueIdentifier, data.Descriptor.ExecutionId); Add(ev, "@attempt", SqlDbType.UniqueIdentifier, data.Lease?.AttemptId); Add(ev, "@state", SqlDbType.NVarChar, state, 40);
            await ev.ExecuteNonQueryAsync(ct);
        }
        if (!data.HoldsJobSlot)
            await WorkerDock.Infrastructure.Dispatch.JobAdmission.AdmitAsync(c, t, data.JobId, ct);
    }
    internal static SqlCommand Command(DbConnection c, DbTransaction t, string sql) => new(sql, (SqlConnection)c, (SqlTransaction)t) { CommandTimeout = 10 };
    internal static void Add(SqlCommand command, string name, SqlDbType type, object? value, int size = 0) =>
        command.Parameters.Add(new SqlParameter(name, type, size) { Value = value ?? DBNull.Value });
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
