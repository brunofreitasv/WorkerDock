using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;

namespace WorkerDock.Infrastructure.Migrations;

public sealed class MigrationRunner(string connectionString, Guid instanceId, string runtimePrincipal, string scriptsRoot)
{
    public ValueTask ApplyAsync(string databaseKind, string targetMigration, CancellationToken ct) =>
        RunAsync(databaseKind, targetMigration, false, ct);

    public ValueTask RollbackAsync(string databaseKind, string targetMigration, CancellationToken ct) =>
        RunAsync(databaseKind, targetMigration, true, ct);

    private async ValueTask RunAsync(string kind, string target, bool rollback, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runtimePrincipal) || runtimePrincipal.Length > 128)
            throw new WorkerDockException("RuntimePrincipalInvalid");
        var (directory, entries, index) = await ReadManifestAsync(kind, target, ct);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await CheckPrerequisitesAsync(connection, ct);
            await ValidateRuntimePrincipalAsync(connection, ct);
            // Keep validation and every target migration under the same session lock.
            // Individual migrations still commit independently.
            await LockAsync(connection, ct);
            try
            {
                var appliedCount = await ValidateAppliedHistoryAsync(connection, entries, ct);
                if (!rollback && appliedCount == 0)
                    await ValidateFreshDatabaseAsync(connection, ct);
                if (rollback)
                {
                    if (index >= appliedCount) throw new WorkerDockException("UnknownMigration");
                    if (index != appliedCount - 1) throw new WorkerDockException("UnsafeRollback");
                    await RollbackOneAsync(connection, directory, entries[index], index == 0, ct);
                }
                else
                {
                    if (appliedCount > index + 1) throw new WorkerDockException("MigrationTargetBehindCurrent");
                    for (var i = appliedCount; i <= index; i++)
                        await ApplyOneAsync(connection, directory, entries[i], ct);
                }
            }
            finally
            {
                // Session locks must be released explicitly before a connection returns to its pool.
                if (connection.State == ConnectionState.Open)
                {
                    await using var release = connection.CreateCommand();
                    release.CommandText = "EXEC sys.sp_releaseapplock @Resource=N'WorkerDock.Schema', @LockOwner=N'Session';";
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }
        catch (SqlException error) when (error.Number == 51002) { throw new WorkerDockException("UnsafeRollback"); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private async Task<(string Directory, List<MigrationEntry> Entries, int Index)> ReadManifestAsync(string kind, string target, CancellationToken ct)
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

        return (directory, entries, index);
    }

    private static async Task CheckPrerequisitesAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), CONVERT(int, compatibility_level) FROM sys.databases WHERE name = DB_NAME()";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("UnsupportedCompatibilityLevel");
        SqlServerPrerequisites.Validate(reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task ValidateRuntimePrincipalAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sys.database_principals WHERE name=@RuntimePrincipal";
        command.Parameters.Add(new SqlParameter("@RuntimePrincipal", SqlDbType.NVarChar, 128) { Value = runtimePrincipal });
        var type = await command.ExecuteScalarAsync(ct);
        if (type is not string principalType || principalType is not ("S" or "U"))
            throw new WorkerDockException("RuntimePrincipalInvalid");
    }

    private static async Task ValidateFreshDatabaseAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped=0) OR EXISTS (SELECT 1 FROM sys.schemas WHERE name NOT IN(N'dbo',N'guest',N'sys',N'INFORMATION_SCHEMA',N'db_owner',N'db_accessadmin',N'db_securityadmin',N'db_ddladmin',N'db_backupoperator',N'db_datareader',N'db_datawriter',N'db_denydatareader',N'db_denydatawriter')) OR EXISTS (SELECT 1 FROM sys.database_principals WHERE type=N'R' AND is_fixed_role=0 AND name<>N'public') OR EXISTS (SELECT 1 FROM sys.types WHERE is_user_defined=1) OR EXISTS (SELECT 1 FROM sys.assemblies WHERE is_user_defined=1) THEN 1 ELSE 0 END";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0)
            throw new WorkerDockException("TenantDatabaseNotEmpty");
    }

    private async Task<int> ValidateAppliedHistoryAsync(SqlConnection connection, IReadOnlyList<MigrationEntry> entries, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = "SELECT SCHEMA_ID(N'workerdock'), OBJECT_ID(N'[workerdock].TenantSettings', N'U'), OBJECT_ID(N'[workerdock].SchemaHistory', N'U')";
            await using var reader = await schema.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (reader.IsDBNull(0))
            {
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                return 0;
            }
            if (reader.IsDBNull(1) || reader.IsDBNull(2))
                throw new WorkerDockException("InvalidMigrationHistory");
        }

        await CheckInstanceAsync(connection, transaction, ct);
        var applied = new Dictionary<string, (string Up, string Down)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT MigrationId, UpChecksum, DownChecksum FROM [workerdock].SchemaHistory";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (!applied.TryAdd(reader.GetString(0), (reader.GetString(1), reader.GetString(2))))
                    throw new WorkerDockException("InvalidMigrationHistory");
        }
        if (applied.Count == 0 || applied.Count > entries.Count)
            throw new WorkerDockException("InvalidMigrationHistory");
        for (var index = 0; index < applied.Count; index++)
        {
            var entry = entries[index];
            if (!applied.TryGetValue(entry.Id, out var history))
                throw new WorkerDockException("InvalidMigrationHistory");
            if (history.Up != entry.UpSha256 || history.Down != entry.DownSha256)
                throw new WorkerDockException("MigrationChecksumMismatch");
        }
        await transaction.CommitAsync(ct);
        return applied.Count;
    }

    private async Task ApplyOneAsync(SqlConnection connection, string directory, MigrationEntry entry, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.GetGuid(0) != instanceId || await reader.ReadAsync(ct))
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

    private static async Task LockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'WorkerDock.Schema', @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=10000; SELECT @result;";
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
            if (batch.Contains("@InstanceId", StringComparison.Ordinal))
                command.Parameters.Add(new SqlParameter("@InstanceId", SqlDbType.UniqueIdentifier) { Value = instanceId });
            if (batch.Contains("@RuntimePrincipal", StringComparison.Ordinal))
                command.Parameters.Add(new SqlParameter("@RuntimePrincipal", SqlDbType.NVarChar, 128) { Value = runtimePrincipal });
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static bool ChecksumMatches(string path, string checksum) => File.Exists(path) &&
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == checksum;

    private sealed record MigrationEntry(string Id, string UpSha256, string DownSha256);
}
