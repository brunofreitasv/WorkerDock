using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;

namespace WorkerDock.DatabaseMigrator;

public sealed class MigrationRunner(string connectionString, Guid instanceId, string scriptsRoot)
{
    public ValueTask ApplyAsync(string databaseKind, string targetMigration, CancellationToken ct) =>
        RunAsync(databaseKind, targetMigration, false, ct);

    public ValueTask RollbackAsync(string databaseKind, string targetMigration, CancellationToken ct) =>
        RunAsync(databaseKind, targetMigration, true, ct);

    private async ValueTask RunAsync(string kind, string target, bool rollback, CancellationToken ct)
    {
        if (kind != "tenant") throw new WorkerDockException("InvalidDatabaseKind");
        var directory = Path.Combine(scriptsRoot, "tenant");
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new WorkerDockException("UnknownMigration");
        var entries = JsonSerializer.Deserialize<List<MigrationEntry>>(await File.ReadAllTextAsync(manifestPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var index = entries.FindIndex(entry => entry.Id == target);
        if (index < 0) throw new WorkerDockException("UnknownMigration");
        foreach (var entry in entries)
        {
            if (!Regex.IsMatch(entry.Id, "^[a-zA-Z0-9_]+$")) throw new WorkerDockException("UnknownMigration");
            if (!ChecksumMatches(Path.Combine(directory, entry.Id + ".up.sql"), entry.UpSha256) ||
                !ChecksumMatches(Path.Combine(directory, entry.Id + ".down.sql"), entry.DownSha256))
                throw new WorkerDockException("MigrationChecksumMismatch");
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await CheckPrerequisitesAsync(connection, ct);
            if (rollback)
                await RollbackOneAsync(connection, directory, entries[index], index == 0, ct);
            else
                for (var i = 0; i <= index; i++) await ApplyOneAsync(connection, directory, entries[i], i == 0, ct);
        }
        catch (SqlException error) when (error.Number == 51002) { throw new WorkerDockException("UnsafeRollback"); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static async Task CheckPrerequisitesAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), CONVERT(int, compatibility_level) FROM sys.databases WHERE name = DB_NAME()";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("UnsupportedCompatibilityLevel");
        SqlServerPrerequisites.Validate(reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task ApplyOneAsync(SqlConnection connection, string directory, MigrationEntry entry, bool bootstrap, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAsync(connection, transaction, ct);
        var installed = await CheckInstanceAsync(connection, transaction, ct);
        if (installed)
        {
            var history = await ReadHistoryAsync(connection, transaction, entry.Id, ct);
            if (history is not null)
            {
                if (history.Value.Up != entry.UpSha256 || history.Value.Down != entry.DownSha256)
                    throw new WorkerDockException("MigrationChecksumMismatch");
                await transaction.CommitAsync(ct);
                return;
            }
        }
        await ExecuteScriptAsync(connection, transaction, Path.Combine(directory, entry.Id + ".up.sql"), ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO [workerdock].SchemaHistory(MigrationId,UpChecksum,DownChecksum,AppliedAtUtc) VALUES (@id,@up,@down,SYSUTCDATETIME())";
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 100) { Value = entry.Id });
        command.Parameters.Add(new SqlParameter("@up", SqlDbType.Char, 64) { Value = entry.UpSha256 });
        command.Parameters.Add(new SqlParameter("@down", SqlDbType.Char, 64) { Value = entry.DownSha256 });
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task RollbackOneAsync(SqlConnection connection, string directory, MigrationEntry entry, bool bootstrap, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await LockAsync(connection, transaction, ct);
        if (!await CheckInstanceAsync(connection, transaction, ct)) throw new WorkerDockException("UnknownMigration");
        var history = await ReadHistoryAsync(connection, transaction, entry.Id, ct);
        if (history is null) throw new WorkerDockException("UnknownMigration");
        if (history.Value.Up != entry.UpSha256 || history.Value.Down != entry.DownSha256)
            throw new WorkerDockException("MigrationChecksumMismatch");
        await ExecuteScriptAsync(connection, transaction, Path.Combine(directory, entry.Id + ".down.sql"), ct);
        if (!bootstrap)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM [workerdock].SchemaHistory WHERE MigrationId=@id";
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 100) { Value = entry.Id });
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<bool> CheckInstanceAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT OBJECT_ID(N'[workerdock].TenantSettings', N'U')";
        var objectId = await exists.ExecuteScalarAsync(ct);
        if (objectId is null or DBNull) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
        if ((Guid?)await command.ExecuteScalarAsync(ct) != instanceId)
            throw new WorkerDockException("TenantInstanceMismatch");
        return true;
    }

    private static async Task<(string Up, string Down)?> ReadHistoryAsync(SqlConnection connection, SqlTransaction transaction, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT UpChecksum, DownChecksum FROM [workerdock].SchemaHistory WHERE MigrationId=@id";
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 100) { Value = id });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'WorkerDock.Schema', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; SELECT @result;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0) throw new WorkerDockException("MigrationLockUnavailable");
    }

    private async Task ExecuteScriptAsync(SqlConnection connection, SqlTransaction transaction, string path, CancellationToken ct)
    {
        var script = await File.ReadAllTextAsync(path, ct);
        foreach (var batch in Regex.Split(script, @"(?im)^\s*GO\s*$"))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = batch;
            command.CommandTimeout = 60;
            command.Parameters.Add(new SqlParameter("@InstanceId", SqlDbType.UniqueIdentifier) { Value = instanceId });
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static bool ChecksumMatches(string path, string checksum) => File.Exists(path) &&
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == checksum;

    private sealed record MigrationEntry(string Id, string UpSha256, string DownSha256);
}
