using System.Data;
using Microsoft.Data.SqlClient;
using WorkerDock.CentralImport;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.IntegrationTests;

public sealed class CentralLegacyImportSqlTests
{
    [Fact]
    public async Task Reads_legacy_sql_rows_and_imports_them_into_a_reopenable_file_registry()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKERDOCK_TEST_SQL"))) return;

        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(snapshot: false);
        var migrator = new WorkerDock.DatabaseMigrator.MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await migrator.ApplyAsync("central", "0001_registry", default);

        var registrationId = Guid.NewGuid();
        var created = DateTimeOffset.Parse("2025-01-02T03:04:05Z");
        var updated = DateTimeOffset.Parse("2025-02-03T04:05:06Z");
        var correlation = Guid.NewGuid();
        await SeedLegacyRowsAsync(database.ConnectionString, registrationId, created, updated, correlation);

        var legacy = await new LegacyCenterReader(database.ConnectionString).ReadAsync(database.InstanceId, default);
        Assert.Equal(database.InstanceId, legacy.InstanceId);
        Assert.Single(legacy.Registrations);
        Assert.Single(legacy.Audit);
        Assert.Single(legacy.Health);

        using var target = new TestDirectory();
        var receipt = await CentralJournalBootstrap.ImportAsync(target.Path, legacy.InstanceId,
            legacy.Registrations, legacy.Audit, legacy.Health, default);
        Assert.Equal(1, receipt.RegistrationCount);
        Assert.Equal(1, receipt.AuditEventCount);
        Assert.Equal(1, receipt.HealthObservationCount);

        using (var registry = FileCentralRegistry.Open(target.Path, database.InstanceId, TimeProvider.System))
        {
            var registered = await registry.GetAsync(registrationId, default);
            Assert.NotNull(registered);
            Assert.Equal("Imported tenant", registered!.DisplayName);
            Assert.Equal(created, registered.CreatedAtUtc);
            Assert.Equal(updated, registered.UpdatedAtUtc);
            Assert.Equal(8, registered.Version.Length);
        }
        using var journal = CentralJournalStore.Open(target.Path, database.InstanceId);
        Assert.Equal(2, journal.Records.Count);
        Assert.Single(journal.Records.Where(record => record.Mutation.Action == "LegacyAudit"));
        Assert.DoesNotContain("HEALTH_REF", journal.Records[1].Mutation.SafeDetailsJson, StringComparison.Ordinal);
    }

    private static async Task SeedLegacyRowsAsync(string connectionString, Guid registrationId, DateTimeOffset created,
        DateTimeOffset updated, Guid correlation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT [workerdock].[TenantRegistrations](Id,DisplayName,ExpectedInstanceId,DashboardUrl,HealthCredentialReference,CreatedAtUtc,UpdatedAtUtc)
            VALUES(@id,N'Imported tenant',@expected,N'https://legacy.example/dashboard/',N'HEALTH_REF',@created,@updated);
            INSERT [workerdock].[TenantHealthObservations](RegistrationId,Health,SafeCode,LastAttemptUtc,LastSuccessUtc)
            VALUES(@id,'Healthy','OK',@updated,@updated);
            INSERT [workerdock].[AuditEvents](Id,AtUtc,ActorId,AuthenticationMode,Action,EntityType,EntityId,CorrelationId,SafeDetails)
            VALUES(@auditId,@updated,N'legacy-admin','Entra','TenantRegistered','TenantRegistration',CONVERT(nvarchar(36),@id),@correlation,N'{"displayName":"Imported tenant"}');
            """;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = registrationId });
        command.Parameters.Add(new SqlParameter("@expected", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Value = created.UtcDateTime });
        command.Parameters.Add(new SqlParameter("@updated", SqlDbType.DateTime2) { Value = updated.UtcDateTime });
        command.Parameters.Add(new SqlParameter("@auditId", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@correlation", SqlDbType.UniqueIdentifier) { Value = correlation });
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workerdock-legacy-import-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
