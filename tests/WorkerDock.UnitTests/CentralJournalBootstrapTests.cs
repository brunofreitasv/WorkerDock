using WorkerDock.Application.Common;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.UnitTests;

public sealed class CentralJournalBootstrapTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Imports_safe_records_preserving_identity_and_latest_health_with_new_versions()
    {
        using var directory = new TestDirectory();
        var registrationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var created = DateTimeOffset.Parse("2024-01-02T03:04:05Z");
        var updated = DateTimeOffset.Parse("2024-02-03T04:05:06Z");
        var auditId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var correlationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var receipt = await CentralJournalBootstrap.ImportAsync(directory.Path, InstanceId,
            [new(registrationId, "Legacy tenant", Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "https://tenant.example/dashboard/", "HEALTH_REF", created, updated)],
            [new(auditId, updated, "legacy-operator", "Entra", "TenantRegistered", "TenantRegistration", registrationId.ToString("D"), correlationId,
                "{\"displayName\":\"Legacy tenant\"}")],
            [new(registrationId, "Unhealthy", "OLD", created.AddHours(1), created.AddHours(1)),
             new(registrationId, "Healthy", "OK", updated, updated)], default);

        Assert.Equal(1, receipt.RegistrationCount);
        Assert.Equal(1, receipt.AuditEventCount);
        Assert.Equal(1, receipt.HealthObservationCount);
        Assert.Matches("^[A-F0-9]{64}$", receipt.JournalSha256);
        Assert.Matches("^[A-F0-9]{64}$", receipt.HealthSnapshotSha256);
        using (var journal = CentralJournalStore.Open(directory.Path, InstanceId))
        {
            Assert.Equal(2, journal.Records.Count);
            var imported = journal.Records[0].Mutation.After!;
            Assert.Equal(registrationId, imported.Id);
            Assert.Equal(created, imported.CreatedAtUtc);
            Assert.Equal(updated, imported.UpdatedAtUtc);
            Assert.Equal("https://tenant.example/dashboard/", imported.DashboardUrl);
            Assert.Equal("ImportedRegistration", journal.Records[0].Mutation.Action);
            var audit = journal.Records[1].Mutation;
            Assert.Equal("LegacyAudit", audit.Action);
            Assert.Equal(updated, audit.AtUtc);
            Assert.Equal("legacy-operator", audit.Actor.Subject);
            Assert.Equal(correlationId, audit.Actor.CorrelationId);
            Assert.Contains(auditId.ToString("D"), audit.SafeDetailsJson);
        }

        using var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System);
        var reopened = await registry.GetAsync(registrationId, default);
        Assert.NotNull(reopened);
        Assert.Equal(8, reopened!.Version.Length);
        Assert.Equal(1L, BitConverter.ToInt64(reopened.Version));

        var health = CentralHealthSnapshot.Open(directory.Path);
        Assert.Equal("Healthy", health.Get(registrationId)!.Health);
        Assert.Equal("OK", health.Get(registrationId)!.SafeCode);
        Assert.Equal(updated, health.Get(registrationId)!.LastAttemptUtc);
    }

    [Fact]
    public async Task Rejects_identity_mismatch_without_creating_target_files()
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => CentralJournalBootstrap.ImportAsync(directory.Path, Guid.Empty, [], [], [], default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Rejects_duplicate_registration_ids_before_writing_any_target_file()
    {
        using var directory = new TestDirectory();
        var registration = Registration(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        await Assert.ThrowsAsync<ArgumentException>(() => CentralJournalBootstrap.ImportAsync(directory.Path, InstanceId,
            [registration, registration], [], [], default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("https://user:pass@tenant.example/", "HEALTH_REF")]
    [InlineData("https://tenant.example/?token=secret", "HEALTH_REF")]
    [InlineData("https://tenant.example/", "credential-reference-with-dashes")]
    public async Task Rejects_unsafe_registration_fields_without_partial_journal(string url, string credentialReference)
    {
        using var directory = new TestDirectory();
        var registration = Registration(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")) with
        {
            DashboardUrl = url,
            HealthCredentialReference = credentialReference
        };

        await Assert.ThrowsAsync<WorkerDockException>(() => CentralJournalBootstrap.ImportAsync(directory.Path, InstanceId, [registration], [], [], default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Rejects_nonempty_target_without_changing_existing_content()
    {
        using var directory = new TestDirectory();
        var sentinel = System.IO.Path.Combine(directory.Path, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CentralJournalBootstrap.ImportAsync(directory.Path, InstanceId, [], [], [], default));
        Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Rejects_secret_bearing_audit_details_without_publishing_a_journal()
    {
        using var directory = new TestDirectory();
        var audit = new LegacyAuditEvent(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            DateTimeOffset.Parse("2024-02-03T04:05:06Z"), "legacy-operator", "Entra", "TenantRegistered",
            "TenantRegistration", null, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "{\"accessToken\":\"sensitive\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => CentralJournalBootstrap.ImportAsync(directory.Path, InstanceId, [], [audit], [], default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private static LegacyRegistration Registration(Guid id) => new(id, "Legacy tenant",
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "https://tenant.example/", "HEALTH_REF",
        DateTimeOffset.Parse("2024-01-02T03:04:05Z"), DateTimeOffset.Parse("2024-02-03T04:05:06Z"));

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workerdock-import-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
