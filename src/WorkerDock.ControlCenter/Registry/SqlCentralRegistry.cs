using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Outbound;
using WorkerDock.Application.Monitoring;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.ControlCenter.Registry;

public sealed class SqlCentralRegistry(string connectionString, Guid centralInstanceId) : ICentralRegistry
{
    public async ValueTask<Page<TenantDirectoryEntry>> ListAsync(string? search, PageRequest page, CancellationToken ct)
    {
        TenantDirectoryCursor.Validate(page);
        if (search?.Length > 200) throw new WorkerDockException("ValidationFailed", "search");
        var cursor = TenantDirectoryCursor.Decode(page.Cursor);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TOP (@limitPlusOne) r.Id,r.DisplayName,r.ExpectedInstanceId,r.DashboardUrl,
                    CASE WHEN h.Health='Healthy' AND h.LastSuccessUtc<DATEADD(minute,-2,SYSUTCDATETIME()) THEN 'Stale' ELSE COALESCE(h.Health,'Unknown') END,
                    h.LastAttemptUtc,h.LastSuccessUtc,r.RowVersion
                FROM [workerdock].TenantRegistrations r LEFT JOIN [workerdock].TenantHealthObservations h ON h.RegistrationId=r.Id
                WHERE (@search IS NULL OR r.DisplayName LIKE N'%' + @search + N'%' ESCAPE N'~')
                  AND (@cursorName IS NULL OR r.DisplayName>@cursorName OR (r.DisplayName=@cursorName AND r.Id>@cursorId))
                ORDER BY r.DisplayName,r.Id;
                """;
            Add(command, "@limitPlusOne", SqlDbType.Int, page.Limit + 1);
            Add(command, "@search", SqlDbType.NVarChar, string.IsNullOrWhiteSpace(search) ? DBNull.Value : EscapeLike(search.Trim()), 200);
            Add(command, "@cursorName", SqlDbType.NVarChar, cursor?.Name is null ? DBNull.Value : cursor.Value.Name, 120);
            Add(command, "@cursorId", SqlDbType.UniqueIdentifier, cursor?.Id is null ? DBNull.Value : cursor.Value.Id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<TenantDirectoryEntry>();
            while (await reader.ReadAsync(ct)) items.Add(ReadDirectory(reader));
            var hasMore = items.Count > page.Limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return new Page<TenantDirectoryEntry>(items, hasMore ? TenantDirectoryCursor.Encode(items[^1]) : null);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<TenantRegistration?> GetAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id,DisplayName,ExpectedInstanceId,DashboardUrl,HealthCredentialReference,RowVersion,CreatedAtUtc,UpdatedAtUtc FROM [workerdock].TenantRegistrations WHERE Id=@id";
            Add(command, "@id", SqlDbType.UniqueIdentifier, id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), new Uri(reader.GetString(3)), new SecretReference(reader.GetString(4)),
                (byte[])reader[5], new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<TenantDirectoryEntry> SaveAsync(Guid? id, TenantRegistrationInput proposed, byte[]? expectedVersion, ActorContext actor, CancellationToken ct)
    {
        var input = TenantRegistrationValidation.Validate(proposed); ValidateActor(actor);
        if ((id is null && expectedVersion is not null) || (id is not null && expectedVersion is null)) throw new WorkerDockException("ValidationFailed", "headers.ifMatch");
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            TenantDirectoryEntry result;
            string action;
            if (id is null)
            {
                var createdId = Guid.NewGuid();
                command.CommandText = "INSERT [workerdock].TenantRegistrations(Id,DisplayName,ExpectedInstanceId,DashboardUrl,HealthCredentialReference) OUTPUT inserted.RowVersion VALUES(@id,@name,@instance,@url,@credential);";
                Add(command, "@id", SqlDbType.UniqueIdentifier, createdId);
                Add(command, "@name", SqlDbType.NVarChar, input.DisplayName, 120);
                Add(command, "@instance", SqlDbType.UniqueIdentifier, input.ExpectedInstanceId);
                Add(command, "@url", SqlDbType.NVarChar, input.DashboardUrl, 2048);
                Add(command, "@credential", SqlDbType.NVarChar, input.HealthCredentialReference, 256);
                var version = (byte[])(await command.ExecuteScalarAsync(ct) ?? throw new WorkerDockException("PersistenceUnavailable"));
                result = new(createdId, input.DisplayName, input.ExpectedInstanceId, new Uri(input.DashboardUrl), "Unknown", null, null, version);
                action = "TenantRegistered";
            }
            else
            {
                command.CommandText = "UPDATE [workerdock].TenantRegistrations SET DisplayName=@name,ExpectedInstanceId=@instance,DashboardUrl=@url,HealthCredentialReference=@credential,UpdatedAtUtc=SYSUTCDATETIME() OUTPUT inserted.RowVersion WHERE Id=@id AND RowVersion=@version;";
                Add(command, "@id", SqlDbType.UniqueIdentifier, id.Value);
                Add(command, "@version", SqlDbType.Timestamp, expectedVersion!, 8);
                Add(command, "@name", SqlDbType.NVarChar, input.DisplayName, 120);
                Add(command, "@instance", SqlDbType.UniqueIdentifier, input.ExpectedInstanceId);
                Add(command, "@url", SqlDbType.NVarChar, input.DashboardUrl, 2048);
                Add(command, "@credential", SqlDbType.NVarChar, input.HealthCredentialReference, 256);
                var version = (byte[]?)await command.ExecuteScalarAsync(ct) ?? throw new WorkerDockException("StaleVersion", "headers.ifMatch");
                result = new(id.Value, input.DisplayName, input.ExpectedInstanceId, new Uri(input.DashboardUrl), "Unknown", null, null, version);
                action = "TenantRegistrationUpdated";
            }
            await WriteAuditAsync(connection, transaction, actor, action, result.Id, new { displayName = input.DisplayName, expectedInstanceId = input.ExpectedInstanceId, dashboardUrl = input.DashboardUrl }, ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask RemoveAsync(Guid id, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        ValidateActor(actor);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM [workerdock].TenantRegistrations OUTPUT deleted.DisplayName WHERE Id=@id AND RowVersion=@version";
            Add(command, "@id", SqlDbType.UniqueIdentifier, id); Add(command, "@version", SqlDbType.Timestamp, expectedVersion, 8);
            var name = (string?)await command.ExecuteScalarAsync(ct);
            if (name is null) throw new WorkerDockException("StaleVersion", "headers.ifMatch");
            await WriteAuditAsync(connection, transaction, actor, "TenantRegistrationRemoved", id, new { displayName = name }, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<TenantDirectoryEntry> RecordHealthAsync(Guid id, string health, string? safeCode, DateTimeOffset attemptedUtc, DateTimeOffset? succeededUtc, CancellationToken ct)
    {
        if (health is not ("Healthy" or "Unhealthy" or "IdentityMismatch" or "Unknown")) throw new WorkerDockException("ValidationFailed");
        safeCode = SafeCode(safeCode);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                UPDATE [workerdock].TenantHealthObservations SET Health=@health,LastAttemptUtc=@attempted,LastSuccessUtc=COALESCE(@succeeded,LastSuccessUtc),SafeCode=@code WHERE RegistrationId=@id;
                IF @@ROWCOUNT=0 INSERT [workerdock].TenantHealthObservations(RegistrationId,Health,LastAttemptUtc,LastSuccessUtc,SafeCode) SELECT @id,@health,@attempted,@succeeded,@code WHERE EXISTS(SELECT 1 FROM [workerdock].TenantRegistrations WHERE Id=@id);
                IF @@ROWCOUNT=0 THROW 51030, 'TenantRegistrationNotFound', 1;
                """;
            Add(command, "@id", SqlDbType.UniqueIdentifier, id); Add(command, "@health", SqlDbType.VarChar, health, 32);
            Add(command, "@attempted", SqlDbType.DateTime2, attemptedUtc.UtcDateTime); Add(command, "@succeeded", SqlDbType.DateTime2, succeededUtc?.UtcDateTime ?? (object)DBNull.Value);
            Add(command, "@code", SqlDbType.VarChar, safeCode is null ? DBNull.Value : safeCode, 64);
            await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
            await using var read = await OpenAsync(ct);
            await using var resultCommand = read.CreateCommand();
            resultCommand.CommandText = """
                SELECT r.Id,r.DisplayName,r.ExpectedInstanceId,r.DashboardUrl,
                    CASE WHEN h.Health='Healthy' AND h.LastSuccessUtc<DATEADD(minute,-2,SYSUTCDATETIME()) THEN 'Stale' ELSE COALESCE(h.Health,'Unknown') END,
                    h.LastAttemptUtc,h.LastSuccessUtc,r.RowVersion
                FROM [workerdock].TenantRegistrations r LEFT JOIN [workerdock].TenantHealthObservations h ON h.RegistrationId=r.Id WHERE r.Id=@id;
                """;
            Add(resultCommand, "@id", SqlDbType.UniqueIdentifier, id);
            await using var reader = await resultCommand.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadDirectory(reader) : throw new WorkerDockException("TenantRegistrationNotFound");
        }
        catch (SqlException error) when (error.Number == 51030) { throw new WorkerDockException("TenantRegistrationNotFound"); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT CentralInstanceId FROM [workerdock].CentralSettings WHERE Singleton=1";
            if ((Guid?)await command.ExecuteScalarAsync(ct) != centralInstanceId) throw new WorkerDockException("CentralInstanceMismatch");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static TenantDirectoryEntry ReadDirectory(SqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), new Uri(reader.GetString(3)), reader.GetString(4),
        reader.IsDBNull(5) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
        reader.IsDBNull(6) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)), (byte[])reader[7]);

    private static async Task WriteAuditAsync(SqlConnection connection, SqlTransaction transaction, ActorContext actor, string action, Guid id, object safeDetails, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT [workerdock].AuditEvents(Id,ActorId,AuthenticationMode,Action,EntityType,EntityId,CorrelationId,SafeDetails) VALUES(@id,@actor,@mode,@action,'TenantRegistration',@entity,@correlation,@details)";
        Add(command, "@id", SqlDbType.UniqueIdentifier, Guid.NewGuid()); Add(command, "@actor", SqlDbType.NVarChar, actor.Subject, 256);
        Add(command, "@mode", SqlDbType.VarChar, actor.AuthenticationMode, 32); Add(command, "@action", SqlDbType.VarChar, action, 64);
        Add(command, "@entity", SqlDbType.NVarChar, id.ToString("D"), 128); Add(command, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
        Add(command, "@details", SqlDbType.NVarChar, JsonSerializer.Serialize(safeDetails), 2000); await command.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateActor(ActorContext actor)
    {
        if (string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 256 || actor.CorrelationId == Guid.Empty) throw new WorkerDockException("InvalidActor");
    }
    private static string? SafeCode(string? value) => value is not null && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;
    private static string EscapeLike(string value) => value.Replace("~", "~~", StringComparison.Ordinal).Replace("%", "~%", StringComparison.Ordinal)
        .Replace("_", "~_", StringComparison.Ordinal).Replace("[", "~[", StringComparison.Ordinal);
    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = new SqlParameter(name, type) { Value = value }; if (size > 0) parameter.Size = size; command.Parameters.Add(parameter);
    }
}
