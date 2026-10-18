using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Settings;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.Infrastructure.Settings;

public sealed class SqlIntegrationSettings(TenantDatabaseOptions options) : IIntegrationSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<VaultSettingsDto> GetVaultAsync(CancellationToken ct) => RunAsync(async (c, t, token) => await ReadVaultAsync(c, t, token), ct);
    public ValueTask<EgressPolicyDto> GetEgressAsync(string name, CancellationToken ct) => RunAsync(async (c, t, token) => await ReadEgressAsync(c, t, name, token), ct);

    public ValueTask<CommandReceipt> SaveVaultAsync(VaultSettingsDto settings, ActorContext actor, CancellationToken ct)
    {
        IntegrationSettingsValidation.ValidateActor(actor);
        var optionsJson = IntegrationSettingsValidation.ValidateVault(settings.ProviderId, settings.PublicOptions);
        return RunAsync(async (c, t, token) =>
        {
            var current = await ReadVaultAsync(c, t, token);
            if (settings.Version is null || !current.Version.AsSpan().SequenceEqual(settings.Version)) throw new WorkerDockException("StaleVersion");
            var revision = await ScalarLongAsync(c, t, "SELECT VaultRevision FROM [workerdock].IntegrationSettings WHERE SettingsId=1", token);
            var next = checked(revision + 1);
            var config = JsonSerializer.Serialize(new { providerId = settings.ProviderId, publicOptions = optionsJson }, JsonOptions);
            await InsertRevisionAsync(c, t, "Vault", "vault", next, config, token);
            await using var update = Command(c, t, "UPDATE [workerdock].IntegrationSettings SET VaultRevision=@revision,ProviderId=@provider,PublicOptionsJson=@options WHERE SettingsId=1 AND Version=@version");
            Add(update, "@revision", SqlDbType.BigInt, next); Add(update, "@provider", SqlDbType.NVarChar, settings.ProviderId, 128);
            Add(update, "@options", SqlDbType.NVarChar, optionsJson.GetRawText(), 4000); Add(update, "@version", SqlDbType.Binary, settings.Version, 8);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
            return await SqlConnectionProfiles.ReceiptAsync(c, t, options.InstanceId, "VaultSettingsSaved", actor, token, beforeRevision: revision, afterRevision: next);
        }, ct);
    }

    public ValueTask<CommandReceipt> SaveEgressAsync(EgressPolicyDto policy, ActorContext actor, CancellationToken ct)
    {
        IntegrationSettingsValidation.ValidateActor(actor);
        var desired = IntegrationSettingsValidation.ValidateEgress(policy);
        return RunAsync(async (c, t, token) =>
        {
            var current = await ReadEgressAsync(c, t, desired.Name, token);
            if (desired.Version is null || !current.Version.AsSpan().SequenceEqual(desired.Version)) throw new WorkerDockException("StaleVersion");
            var revision = await ScalarLongAsync(c, t, "SELECT Revision FROM [workerdock].TenantEgressPolicies WHERE Name=@name", token, ("@name", SqlDbType.NVarChar, desired.Name, 32));
            var next = checked(revision + 1);
            var config = JsonSerializer.Serialize(new { desired.Name, desired.Hosts, desired.Ports, desired.PrivateCidrs, desired.AllowHttp }, JsonOptions);
            await InsertRevisionAsync(c, t, "Egress", desired.Name, next, config, token);
            await using var update = Command(c, t, "UPDATE [workerdock].TenantEgressPolicies SET Revision=@revision,HostsJson=@hosts,PortsJson=@ports,PrivateCidrsJson=@cidrs,AllowHttp=@http WHERE Name=@name AND Version=@version");
            Add(update, "@revision", SqlDbType.BigInt, next); Add(update, "@hosts", SqlDbType.NVarChar, JsonSerializer.Serialize(desired.Hosts, JsonOptions), 4000);
            Add(update, "@ports", SqlDbType.NVarChar, JsonSerializer.Serialize(desired.Ports, JsonOptions), 4000); Add(update, "@cidrs", SqlDbType.NVarChar, JsonSerializer.Serialize(desired.PrivateCidrs, JsonOptions), 4000);
            Add(update, "@http", SqlDbType.Bit, desired.AllowHttp); Add(update, "@name", SqlDbType.NVarChar, desired.Name, 32); Add(update, "@version", SqlDbType.Binary, desired.Version, 8);
            if (await update.ExecuteNonQueryAsync(token) != 1) throw new WorkerDockException("StaleVersion");
            return await SqlConnectionProfiles.ReceiptAsync(c, t, options.InstanceId, "EgressSettingsSaved", actor, token, beforeRevision: revision, afterRevision: next);
        }, ct);
    }

    public ValueTask<IntegrationConfigurationDto> GetSnapshotAsync(CancellationToken ct) => RunAsync(async (c, t, token) =>
        await ReadSnapshotAsync(c, t, token), ct);

    internal static async ValueTask<IntegrationConfigurationDto> ReadSnapshotAsync(DbConnection c, DbTransaction t, CancellationToken ct)
    {
        var vault = await ReadVaultAsync(c, t, ct);
        var revision = await ScalarLongAsync(c, t, "SELECT VaultRevision FROM [workerdock].IntegrationSettings WHERE SettingsId=1", ct);
        var policies = new List<EgressPolicyDto>(2);
        foreach (var name in IntegrationSettingsValidation.Names) policies.Add(await ReadEgressAsync(c, t, name, ct));
        return IntegrationSettingsValidation.ValidateSnapshot(new IntegrationConfigurationDto(1, revision, vault.ProviderId, vault.PublicOptions, policies));
    }

    private async ValueTask<T> RunAsync<T>(Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (c, t, token) =>
            {
                await using var identity = Command(c, t, "SELECT InstanceId FROM [workerdock].TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                return await action(c, t, token);
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static async ValueTask<VaultSettingsDto> ReadVaultAsync(DbConnection c, DbTransaction t, CancellationToken ct)
    {
        await using var command = Command(c, t, "SELECT ProviderId,PublicOptionsJson,Version FROM [workerdock].IntegrationSettings WHERE SettingsId=1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("SettingsUnavailable");
        var provider = reader.GetString(0); using var doc = JsonDocument.Parse(reader.GetString(1));
        var options = IntegrationSettingsValidation.ValidateVault(provider, doc.RootElement);
        return new(provider, options, reader.GetFieldValue<byte[]>(2));
    }

    private static async ValueTask<EgressPolicyDto> ReadEgressAsync(DbConnection c, DbTransaction t, string name, CancellationToken ct)
    {
        if (!IntegrationSettingsValidation.Names.Contains(name, StringComparer.Ordinal)) throw new WorkerDockException("EgressPolicyNotFound");
        await using var command = Command(c, t, "SELECT HostsJson,PortsJson,PrivateCidrsJson,AllowHttp,Version FROM [workerdock].TenantEgressPolicies WHERE Name=@name");
        Add(command, "@name", SqlDbType.NVarChar, name, 32); await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new WorkerDockException("EgressPolicyNotFound");
        return IntegrationSettingsValidation.ValidateEgress(new(name, JsonSerializer.Deserialize<string[]>(reader.GetString(0), JsonOptions) ?? [], JsonSerializer.Deserialize<int[]>(reader.GetString(1), JsonOptions) ?? [],
            JsonSerializer.Deserialize<string[]>(reader.GetString(2), JsonOptions) ?? [], reader.GetBoolean(3), reader.GetFieldValue<byte[]>(4)));
    }

    private static async ValueTask InsertRevisionAsync(DbConnection c, DbTransaction t, string kind, string name, long revision, string config, CancellationToken ct)
    {
        await using var command = Command(c, t, "INSERT [workerdock].IntegrationSettingsRevisions(RevisionId,SettingsKind,SettingsName,Revision,ConfigurationJson,CreatedAtUtc) VALUES(NEWID(),@kind,@name,@revision,@configuration,SYSUTCDATETIME())");
        Add(command, "@kind", SqlDbType.NVarChar, kind, 16); Add(command, "@name", SqlDbType.NVarChar, name, 32); Add(command, "@revision", SqlDbType.BigInt, revision); Add(command, "@configuration", SqlDbType.NVarChar, config, -1);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask<long> ScalarLongAsync(DbConnection c, DbTransaction t, string sql, CancellationToken ct, params (string Name, SqlDbType Type, object Value, int Size)[] args)
    {
        await using var command = Command(c, t, sql); foreach (var arg in args) Add(command, arg.Name, arg.Type, arg.Value, arg.Size);
        var value = await command.ExecuteScalarAsync(ct); return value is long number ? number : throw new WorkerDockException("SettingsUnavailable");
    }
    private static SqlCommand Command(DbConnection c, DbTransaction t, string sql) => new(sql, (SqlConnection)c, (SqlTransaction)t) { CommandTimeout = 10 };
    private static void Add(SqlCommand command, string name, SqlDbType type, object? value, int size = 0) => command.Parameters.Add(new SqlParameter(name, type, size) { Value = value ?? DBNull.Value });
}
