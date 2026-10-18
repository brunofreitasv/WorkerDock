using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WorkerDock.Application.Common;
using WorkerDock.Application.Tenants;
using WorkerDock.Domain.Tenants;

namespace WorkerDock.Infrastructure.Persistence;

public sealed class TenantSettingsService(TenantDatabaseOptions options) : ITenantSettingsService
{
    public async ValueTask<TenantSettings> GetAsync(CancellationToken ct)
    {
        try
        {
            await using var context = new TenantSettingsDbContext(options.ConnectionString);
            var row = await context.Settings.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null || row.InstanceId != options.InstanceId)
                throw new WorkerDockException("TenantInstanceMismatch");
            return new(row.InstanceId, row.DisplayName, row.DisplayTimeZone, row.Version);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    public async ValueTask<TenantSettings> UpdateAsync(TenantSettings desired, byte[] expectedVersion, ActorContext actor, CancellationToken ct)
    {
        TenantSettingsValidation.Validate(desired, expectedVersion, actor, options.InstanceId);
        try
        {
            return await new BusinessTransaction(options).RunAsync<TenantSettings>(async (connection, transaction, token) =>
            {
                await using var identity = connection.CreateCommand();
                identity.Transaction = transaction;
                identity.CommandText = "SELECT InstanceId FROM [workerdock].TenantSettings";
                var storedId = (Guid?)await identity.ExecuteScalarAsync(token);
                if (storedId != options.InstanceId) throw new WorkerDockException("TenantInstanceMismatch");

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE [workerdock].TenantSettings SET DisplayName=@name, DisplayTimeZone=@zone OUTPUT inserted.Version WHERE InstanceId=@id AND Version=@version";
                Add(update, "@name", SqlDbType.NVarChar, desired.DisplayName, 200);
                Add(update, "@zone", SqlDbType.NVarChar, desired.DisplayTimeZone, 100);
                Add(update, "@id", SqlDbType.UniqueIdentifier, options.InstanceId);
                Add(update, "@version", SqlDbType.Timestamp, expectedVersion, 8);
                var version = (byte[]?)await update.ExecuteScalarAsync(token);
                if (version is null) throw new WorkerDockException("StaleVersion");

                await using var audit = connection.CreateCommand();
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO [workerdock].AuditEvents (EventId,EventType,ActorSubject,AuthenticationMode,CorrelationId,OccurredAtUtc,DetailsJson) VALUES (@eventId,N'TenantSettingsUpdated',@actor,@mode,@correlation,SYSUTCDATETIME(),N'{}')";
                Add(audit, "@eventId", SqlDbType.UniqueIdentifier, Guid.NewGuid());
                Add(audit, "@actor", SqlDbType.NVarChar, actor.Subject, 300);
                Add(audit, "@mode", SqlDbType.NVarChar, actor.AuthenticationMode, 40);
                Add(audit, "@correlation", SqlDbType.UniqueIdentifier, actor.CorrelationId);
                await audit.ExecuteNonQueryAsync(token);
                return desired with { Version = version };
            }, ct);
        }
        catch (SqlException) { throw new WorkerDockException("PersistenceUnavailable"); }
    }

    private static void Add(System.Data.Common.DbCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = new SqlParameter(name, type) { Value = value };
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}

internal sealed class TenantSettingsDbContext(string connectionString) : DbContext
{
    public DbSet<TenantSettingsRow> Settings => Set<TenantSettingsRow>();
    protected override void OnConfiguring(DbContextOptionsBuilder builder) => builder.UseSqlServer(connectionString);
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<TenantSettingsRow>(entity =>
        {
            entity.ToTable("TenantSettings", "workerdock");
            entity.HasKey(x => x.InstanceId);
            entity.Property(x => x.Version).IsRowVersion();
        });
    }
}

internal sealed class TenantSettingsRow
{
    public Guid InstanceId { get; set; }
    public string DisplayName { get; set; } = "";
    public string DisplayTimeZone { get; set; } = "";
    public byte[] Version { get; set; } = [];
}
