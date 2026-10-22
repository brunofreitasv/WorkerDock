using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;

namespace WorkerDock.DatabaseMigrator;

public sealed partial class MigrationRunner
{
    public async ValueTask PrepareUpgradeAsync(string databaseKind, string targetMigration, string backupPath, CancellationToken ct)
    {
        var (_, entries, targetIndex) = await ReadManifestAsync(databaseKind, targetMigration, ct);
        ValidateBackupPath(backupPath);
        var (minimum, maximum) = await ReadPackageCompatibilityAsync(ct);
        ValidateSchemaVersion(targetMigration, minimum, maximum);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await CheckPrerequisitesAsync(connection, ct);
            // BACKUP cannot run in a transaction. Keep a session lock through validation and verification.
            await using (var schemaLock = connection.CreateCommand())
            {
                schemaLock.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'WorkerDock.Schema', @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=10000; SELECT @result;";
                if (Convert.ToInt32(await schemaLock.ExecuteScalarAsync(ct)) < 0)
                    throw new WorkerDockException("MigrationLockUnavailable");
            }
            try
            {
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct))
                {
                    if (!await CheckInstanceAsync(connection, transaction, ct))
                        throw new WorkerDockException("InvalidMigrationHistory");
                    await ValidateUpgradeHistoryAsync(connection, transaction, entries, targetIndex, minimum, maximum, ct);
                    await transaction.CommitAsync(ct);
                }
                ValidateBackupPath(backupPath);
                await using var backup = connection.CreateCommand();
                backup.CommandTimeout = 0;
                var database = "[" + connection.Database.Replace("]", "]]") + "]";
                backup.CommandText = $"""
                    IF EXISTS (SELECT 1 FROM sys.dm_os_file_exists(@backup) WHERE file_exists=1 OR file_is_a_directory=1)
                        THROW 51003, 'BackupAlreadyExists', 1;
                    BACKUP DATABASE {database} TO DISK=@backup WITH COPY_ONLY, CHECKSUM;
                    RESTORE VERIFYONLY FROM DISK=@backup WITH CHECKSUM;
                    """;
                backup.Parameters.Add(new SqlParameter("@backup", SqlDbType.NVarChar, 4000) { Value = backupPath });
                await backup.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                // Explicitly release the session lock before returning the connection to its pool.
                await using var release = connection.CreateCommand();
                release.CommandText = "EXEC sys.sp_releaseapplock @Resource=N'WorkerDock.Schema', @LockOwner=N'Session';";
                if (connection.State == ConnectionState.Open) await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (SqlException error) when (error.Number == 51003) { throw new WorkerDockException("BackupAlreadyExists"); }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static void ValidateBackupPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new WorkerDockException("BackupAlreadyExists");
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4000 || !path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || path.Any(c => char.IsControl(c) || "<>:\"/|?*".Contains(c)))
            throw new WorkerDockException("InvalidBackupPath");
        var parts = path[2..].Split('\\');
        if (parts.Length < 3 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new WorkerDockException("InvalidBackupPath");
    }

    private async Task<(int Minimum, int Maximum)> ReadPackageCompatibilityAsync(CancellationToken ct)
    {
        var path = Path.GetFullPath(Path.Combine(scriptsRoot, "..", "..", "..", "manifest.json"));
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var range = manifest.RootElement.GetProperty("schemaCompatibility");
            var minimum = range.GetProperty("minimum").GetInt32();
            var maximum = range.GetProperty("maximum").GetInt32();
            if (minimum < 1 || maximum < minimum) throw new WorkerDockException("InvalidPackageManifest");
            return (minimum, maximum);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new WorkerDockException("InvalidPackageManifest");
        }
    }

    private static void ValidateSchemaVersion(string migration, int minimum, int maximum)
    {
        if (!int.TryParse(Regex.Match(migration, @"^\d+").Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < minimum || version > maximum)
            throw new WorkerDockException("UnsupportedSchemaVersion");
    }

    private static async Task ValidateUpgradeHistoryAsync(SqlConnection connection, SqlTransaction transaction,
        List<MigrationEntry> entries, int targetIndex, int minimum, int maximum, CancellationToken ct)
    {
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
        if (applied.Count == 0 || applied.Count > targetIndex + 1)
            throw new WorkerDockException("InvalidMigrationHistory");
        for (var index = 0; index < applied.Count; index++)
        {
            var entry = entries[index];
            if (!applied.TryGetValue(entry.Id, out var history)) throw new WorkerDockException("InvalidMigrationHistory");
            if (history.Up != entry.UpSha256 || history.Down != entry.DownSha256)
                throw new WorkerDockException("MigrationChecksumMismatch");
        }
        ValidateSchemaVersion(entries[applied.Count - 1].Id, minimum, maximum);
    }
}
