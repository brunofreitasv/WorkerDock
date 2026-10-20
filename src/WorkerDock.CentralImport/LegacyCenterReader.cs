using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.CentralImport;

public sealed record LegacyCenterData(Guid InstanceId, IReadOnlyList<LegacyRegistration> Registrations,
    IReadOnlyList<LegacyAuditEvent> Audit, IReadOnlyList<LegacyHealthObservation> Health);

public sealed class LegacyCenterReader(string connectionString)
{
    public async Task<LegacyCenterData> ReadAsync(Guid expectedInstanceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A legacy SQL connection is required.", nameof(connectionString));
        if (expectedInstanceId == Guid.Empty) throw new ArgumentException("Center instance ID is required.", nameof(expectedInstanceId));
        var expectedMigration = await ReadExpectedMigrationAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var instanceId = await ReadInstanceIdAsync(connection, ct);
        if (instanceId != expectedInstanceId) throw new InvalidDataException("Legacy center identity does not match the requested instance.");
        await VerifyMigrationHistoryAsync(connection, expectedMigration, ct);

        var registrations = await ReadRegistrationsAsync(connection, ct);
        var audit = await ReadAuditAsync(connection, ct);
        var health = await ReadHealthAsync(connection, ct);
        return new LegacyCenterData(instanceId, registrations, audit, health);
    }

    private static async Task<Guid> ReadInstanceIdAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CentralInstanceId FROM [workerdock].[CentralSettings] WHERE Singleton=1";
        var result = await command.ExecuteScalarAsync(ct);
        if (result is not Guid id || id == Guid.Empty) throw new InvalidDataException("Legacy center identity is missing or invalid.");
        return id;
    }

    private static async Task<MigrationManifestEntry> ReadExpectedMigrationAsync(CancellationToken ct)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "database", "central");
        var entries = JsonSerializer.Deserialize<List<MigrationManifestEntry>>(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var entry = entries.SingleOrDefault(item => item.Id == "0001_registry")
            ?? throw new InvalidDataException("Legacy central migration manifest is invalid.");
        foreach (var (name, checksum) in new[] { ("0001_registry.up.sql", entry.UpSha256), ("0001_registry.down.sql", entry.DownSha256) })
        {
            var script = Path.Combine(root, name);
            if (!File.Exists(script) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script, ct))) != checksum)
                throw new InvalidDataException("Legacy central migration manifest checksum failed.");
        }
        return entry;
    }

    private static async Task VerifyMigrationHistoryAsync(SqlConnection connection, MigrationManifestEntry expected, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UpChecksum, DownChecksum FROM [workerdock].[SchemaHistory] WHERE MigrationId=@id";
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.NVarChar, 100) { Value = expected.Id });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !string.Equals(reader.GetString(0).Trim(), expected.UpSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(1).Trim(), expected.DownSha256, StringComparison.OrdinalIgnoreCase) || await reader.ReadAsync(ct))
            throw new InvalidDataException("Legacy central migration history does not match the installed scripts.");
    }

    private static async Task<IReadOnlyList<LegacyRegistration>> ReadRegistrationsAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,DisplayName,ExpectedInstanceId,DashboardUrl,HealthCredentialReference,CreatedAtUtc,UpdatedAtUtc FROM [workerdock].[TenantRegistrations] ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LegacyRegistration>();
        while (await reader.ReadAsync(ct))
            rows.Add(new LegacyRegistration(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), Utc(reader.GetDateTime(5)), Utc(reader.GetDateTime(6))));
        return rows;
    }

    private static async Task<IReadOnlyList<LegacyAuditEvent>> ReadAuditAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,AtUtc,ActorId,AuthenticationMode,Action,EntityType,EntityId,CorrelationId,SafeDetails FROM [workerdock].[AuditEvents] ORDER BY AtUtc,Id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LegacyAuditEvent>();
        while (await reader.ReadAsync(ct))
            rows.Add(new LegacyAuditEvent(reader.GetGuid(0), Utc(reader.GetDateTime(1)), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetGuid(7), reader.IsDBNull(8) ? "{}" : reader.GetString(8)));
        return rows;
    }

    private static async Task<IReadOnlyList<LegacyHealthObservation>> ReadHealthAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RegistrationId,Health,SafeCode,LastAttemptUtc,LastSuccessUtc FROM [workerdock].[TenantHealthObservations] ORDER BY RegistrationId";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LegacyHealthObservation>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(3)) throw new InvalidDataException("Legacy health observation has no attempt timestamp.");
            rows.Add(new LegacyHealthObservation(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                Utc(reader.GetDateTime(3)), reader.IsDBNull(4) ? null : Utc(reader.GetDateTime(4))));
        }
        return rows;
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record MigrationManifestEntry(string Id, string UpSha256, string DownSha256);
}
