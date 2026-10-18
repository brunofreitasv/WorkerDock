using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Persistence;
using static WorkerDock.Infrastructure.Executions.SqlAttemptStore;

namespace WorkerDock.Infrastructure.Workers;

public sealed class SqlCapacityStore(TenantDatabaseOptions options) : ICapacityStore
{
    public static readonly Guid PolicyId = new("c9188146-bcab-43ec-95ee-c4713f46f90d");
    public async ValueTask<T> TransactAsync<T>(Func<CapacityState, DateTimeOffset, T> change, CancellationToken ct)
    {
        try
        {
            return await new BusinessTransaction(options).RunAsync(async (c, t, token) =>
            {
                await using var identity = Command(c, t, "SELECT InstanceId FROM dbo.TenantSettings");
                if ((Guid?)await identity.ExecuteScalarAsync(token) != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");
                await using var read = Command(c, t, """
                    SELECT SYSUTCDATETIME();
                    SELECT TemplateId,TemplateVersion,MetadataDigest FROM dbo.TemplateCatalog WHERE Enabled=1 ORDER BY TemplateId COLLATE Latin1_General_100_BIN2,TemplateVersion COLLATE Latin1_General_100_BIN2;
                    SELECT TemplateId,DesiredLimit FROM dbo.TemplatePolicies;
                    SELECT DesiredRevision,EffectiveRevision,State FROM dbo.PolicyRevisions WHERE PolicyId=@id;
                    SELECT InstallationId,BootId,CatalogDigest,CapacityCeiling,LiveLimit,AcknowledgedRevision,State,LastHeartbeatUtc,ReservationsRecovered FROM dbo.Workers;
                    """);
                Add(read, "@id", SqlDbType.UniqueIdentifier, PolicyId);
                await using var reader = await read.ExecuteReaderAsync(token);
                await reader.ReadAsync(token); var now = Utc(reader.GetDateTime(0));
                await reader.NextResultAsync(token); var catalog = new StringBuilder();
                while (await reader.ReadAsync(token)) catalog.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('|').Append(reader.GetString(2)).Append('\n');
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.ToString())));
                await reader.NextResultAsync(token); var limits = new Dictionary<string, int>(StringComparer.Ordinal);
                while (await reader.ReadAsync(token)) limits.Add(reader.GetString(0), reader.GetInt32(1));
                await reader.NextResultAsync(token); await reader.ReadAsync(token);
                var desired = reader.GetInt64(0); var effective = reader.GetInt64(1); var state = reader.GetString(2);
                await reader.NextResultAsync(token); var workers = new List<CapacityWorker>();
                while (await reader.ReadAsync(token)) workers.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.GetInt64(5), reader.GetString(6), Utc(reader.GetDateTime(7)), reader.GetBoolean(8)));
                await reader.CloseAsync();
                var data = new CapacityState(digest, limits, desired, effective, state, workers);
                var result = change(data, now);
                await using var policy = Command(c, t, "UPDATE dbo.PolicyRevisions SET DesiredRevision=@desired,EffectiveRevision=@effective,State=@state,CatalogDigest=@digest WHERE PolicyId=@id;");
                Add(policy, "@desired", SqlDbType.BigInt, data.DesiredRevision); Add(policy, "@effective", SqlDbType.BigInt, data.EffectiveRevision);
                Add(policy, "@state", SqlDbType.NVarChar, data.State, 30); Add(policy, "@digest", SqlDbType.Char, digest, 64); Add(policy, "@id", SqlDbType.UniqueIdentifier, PolicyId);
                await policy.ExecuteNonQueryAsync(token);
                foreach (var limit in data.Limits)
                {
                    await using var update = Command(c, t, "UPDATE dbo.TemplatePolicies SET DesiredLimit=@limit,DesiredRevision=@desired,EffectiveRevision=@effective,StartGateClosed=@closed,EffectiveLimit=CASE WHEN @closed=0 THEN @limit ELSE EffectiveLimit END WHERE TemplateId=@template");
                    Add(update, "@limit", SqlDbType.Int, limit.Value); Add(update, "@desired", SqlDbType.BigInt, data.DesiredRevision); Add(update, "@effective", SqlDbType.BigInt, data.EffectiveRevision);
                    Add(update, "@closed", SqlDbType.Bit, data.State != "Effective"); Add(update, "@template", SqlDbType.NVarChar, limit.Key, 100); await update.ExecuteNonQueryAsync(token);
                }
                foreach (var w in data.Workers.Values)
                {
                    await using var update = Command(c, t, """
                        UPDATE dbo.Workers SET BootId=@boot,CatalogDigest=@digest,CapacityCeiling=@ceiling,LiveLimit=@live,AcknowledgedRevision=@revision,State=@state,LastHeartbeatUtc=@now,ReservationsRecovered=@recovered WHERE InstallationId=@id;
                        IF @@ROWCOUNT=0 INSERT dbo.Workers(InstallationId,BootId,CatalogDigest,CapacityCeiling,LiveLimit,AcknowledgedRevision,State,LastHeartbeatUtc,ReservationsRecovered)
                            VALUES(@id,@boot,@digest,@ceiling,@live,@revision,@state,@now,@recovered);
                        DELETE FROM dbo.WorkerCapabilities WHERE InstallationId=@id;
                        IF @state<>N'CatalogMismatch' INSERT dbo.WorkerCapabilities SELECT @id,TemplateId,TemplateVersion FROM dbo.TemplateCatalog WHERE Enabled=1;
                        """);
                    Add(update, "@id", SqlDbType.UniqueIdentifier, w.InstallationId); Add(update, "@boot", SqlDbType.NVarChar, w.BootId, 100);
                    Add(update, "@digest", SqlDbType.Char, w.CatalogDigest, 64); Add(update, "@ceiling", SqlDbType.Int, w.Ceiling); Add(update, "@live", SqlDbType.Int, w.LiveLimit);
                    Add(update, "@revision", SqlDbType.BigInt, w.AcknowledgedRevision); Add(update, "@state", SqlDbType.NVarChar, w.State, 30);
                    Add(update, "@now", SqlDbType.DateTime2, w.HeartbeatUtc.UtcDateTime); Add(update, "@recovered", SqlDbType.Bit, w.ReservationsRecovered); await update.ExecuteNonQueryAsync(token);
                }
                if (data.State == "Effective")
                {
                    await using var reopen = Command(c, t, "UPDATE dbo.Commands SET State=N'Applied' WHERE CommandKind=N'TemplateLimit' AND State=N'Pending' AND PolicyRevision<=@revision;");
                    Add(reopen, "@revision", SqlDbType.BigInt, data.EffectiveRevision); await reopen.ExecuteNonQueryAsync(token);
                }
                return result;
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
