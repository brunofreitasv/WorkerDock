using WorkerDock.Application.Common;
using WorkerDock.Application.Monitoring;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.UnitTests;

public sealed class FileCentralRegistryTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ActorContext Actor = new("operator", "Entra", Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public async Task Create_update_delete_enforce_etags_and_replay_search_pagination()
    {
        using var directory = new TestDirectory();
        Guid firstId;
        byte[] originalVersion;
        using (var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System))
        {
            var first = await registry.SaveAsync(null, Input("Alpha"), null, Actor, default);
            firstId = first.Id;
            originalVersion = first.Version;
            Assert.Equal(8, first.Version.Length);
            Assert.Equal(1L, BitConverter.ToInt64(first.Version));
            var updated = await registry.SaveAsync(first.Id, Input("Alpine"), first.Version, Actor, default);
            Assert.Equal(2L, BitConverter.ToInt64(updated.Version));
            await Assert.ThrowsAsync<WorkerDockException>(async () => await registry.SaveAsync(first.Id, Input("Stale"), originalVersion, Actor, default));

            await registry.SaveAsync(null, Input("Beta"), null, Actor, default);
        }

        using (var reopened = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System))
        {
            var page = await reopened.ListAsync("alp", new PageRequest(null, 1), default);
            Assert.Equal("Alpine", Assert.Single(page.Items).DisplayName);
            Assert.Null(page.NextCursor);
            var firstPage = await reopened.ListAsync(null, new PageRequest(null, 1), default);
            Assert.Equal("Alpine", Assert.Single(firstPage.Items).DisplayName);
            Assert.NotNull(firstPage.NextCursor);
            var secondPage = await reopened.ListAsync(null, new PageRequest(firstPage.NextCursor, 1), default);
            Assert.Equal("Beta", Assert.Single(secondPage.Items).DisplayName);
            Assert.Null(secondPage.NextCursor);
            var first = await reopened.GetAsync(firstId, default);
            Assert.Equal("Alpine", first!.DisplayName);
            await reopened.RemoveAsync(firstId, first.Version, Actor, default);
            await Assert.ThrowsAsync<WorkerDockException>(async () => await reopened.RemoveAsync(firstId, first.Version, Actor, default));
        }
    }

    [Fact]
    public async Task Mutation_and_sanitized_before_after_audit_share_one_journal_event()
    {
        using var directory = new TestDirectory();
        var healthCredential = "HEALTH_REF";
        using (var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System))
        {
            var created = await registry.SaveAsync(null, Input("Alpha", healthCredential), null, Actor, default);
            await registry.SaveAsync(created.Id, Input("Beta", healthCredential), created.Version, Actor, default);
        }

        using var journal = CentralJournalStore.Open(directory.Path, InstanceId);
        Assert.Equal(2, journal.Records.Count);
        var update = journal.Records[1].Mutation;
        Assert.Equal("Updated", update.Action);
        Assert.Equal(Actor, update.Actor);
        Assert.Equal(Actor.CorrelationId, update.Actor.CorrelationId);
        Assert.Equal("Alpha", update.Before!.DisplayName);
        Assert.Equal("Beta", update.After!.DisplayName);
        Assert.Contains("Alpha", update.SafeDetailsJson);
        Assert.Contains("Beta", update.SafeDetailsJson);
        Assert.DoesNotContain(healthCredential, update.SafeDetailsJson);
        Assert.DoesNotContain("resolved-secret", update.SafeDetailsJson);
    }

    [Fact]
    public async Task Legacy_audit_frame_does_not_remove_a_registration_during_replay()
    {
        using var directory = new TestDirectory();
        Guid id;
        using (var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System))
            id = (await registry.SaveAsync(null, Input("Alpha"), null, Actor, default)).Id;
        using (var journal = CentralJournalStore.Open(directory.Path, InstanceId))
            await journal.AppendAsync(new CentralJournalMutation("LegacyAudit", id, null, null, DateTimeOffset.UtcNow,
                Actor, "{}"), default);

        using var reopened = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System);
        Assert.Equal("Alpha", (await reopened.GetAsync(id, default))!.DisplayName);
    }

    [Fact]
    public async Task Health_snapshot_survives_restart_and_healthy_projection_becomes_stale()
    {
        using var directory = new TestDirectory();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        Guid id;
        using (var registry = FileCentralRegistry.Open(directory.Path, InstanceId, clock))
        {
            id = (await registry.SaveAsync(null, Input("Alpha"), null, Actor, default)).Id;
            await registry.RecordHealthAsync(id, "Healthy", null, clock.GetUtcNow(), clock.GetUtcNow(), default);
        }

        clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        using var reopened = FileCentralRegistry.Open(directory.Path, InstanceId, clock);
        var entry = Assert.Single((await reopened.ListAsync(null, new PageRequest(null, 10), default)).Items);
        Assert.Equal("Stale", entry.Health);
        Assert.NotNull(entry.LastSuccessUtc);
    }

    [Fact]
    public async Task Corrupt_health_snapshot_projects_unknown()
    {
        using var directory = new TestDirectory();
        Guid id;
        using (var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System))
        {
            id = (await registry.SaveAsync(null, Input("Alpha"), null, Actor, default)).Id;
            await registry.RecordHealthAsync(id, "Healthy", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default);
        }
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "health.json"), "not json");

        using var reopened = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System);
        var entry = Assert.Single((await reopened.ListAsync(null, new PageRequest(null, 10), default)).Items);
        Assert.Equal("Unknown", entry.Health);
        Assert.Null(entry.LastAttemptUtc);
    }

    [Fact]
    public async Task Failed_health_snapshot_replace_keeps_previous_live_observation()
    {
        using var directory = new TestDirectory();
        var id = Guid.NewGuid();
        var original = new HealthObservation("Healthy", null, DateTimeOffset.Parse("2026-10-19T12:00:00Z"), DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        var initial = CentralHealthSnapshot.Open(directory.Path);
        {
            await initial.SaveAsync(id, original, default);
        }

        var snapshot = CentralHealthSnapshot.Open(directory.Path, (_, _, _) =>
            ValueTask.FromException(new IOException("simulated snapshot write failure")));
        await Assert.ThrowsAsync<IOException>(async () => await snapshot.SaveAsync(id,
            original with { Health = "Unhealthy" }, default));
        Assert.Equal(original, snapshot.Get(id));
        var reopened = CentralHealthSnapshot.Open(directory.Path);
        Assert.Equal(original, reopened.Get(id));
    }

    [Fact]
    public async Task Health_refresh_racing_remove_never_resurrects_registration()
    {
        using var directory = new TestDirectory();
        using var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System);
        var created = await registry.SaveAsync(null, Input("Alpha"), null, Actor, default);
        var refresh = RecordHealthOrMissingAsync(registry, created.Id);
        var remove = registry.RemoveAsync(created.Id, created.Version, Actor, default).AsTask();
        await Task.WhenAll(refresh, remove);
        Assert.Null(await registry.GetAsync(created.Id, default));
        var empty = await registry.ListAsync(null, new PageRequest(null, 10), default);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task Failed_append_returns_no_success_and_projects_no_edit()
    {
        using var directory = new TestDirectory();
        using var registry = FileCentralRegistry.Open(directory.Path, InstanceId, TimeProvider.System,
            stream => new FailingWriter(stream));
        await Assert.ThrowsAsync<IOException>(async () =>
            await registry.SaveAsync(null, Input("Alpha"), null, Actor, default));
        Assert.Empty((await registry.ListAsync(null, new PageRequest(null, 10), default)).Items);
    }

    private static async Task RecordHealthOrMissingAsync(FileCentralRegistry registry, Guid id)
    {
        try { await registry.RecordHealthAsync(id, "Healthy", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default); }
        catch (WorkerDockException error) { Assert.Equal("TenantRegistrationNotFound", error.Code); }
    }

    private static TenantRegistrationInput Input(string name, string credential = "HEALTH_REF") =>
        new(name, Guid.Parse("33333333-3333-3333-3333-333333333333"), "https://tenant.example/", credential);

    private sealed class FailingWriter(FileStream stream) : ICentralJournalWriter
    {
        public async ValueTask AppendAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            await stream.WriteAsync(frame[..Math.Min(5, frame.Length)], ct);
            throw new IOException("simulated append failure");
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workerdock-registry-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
