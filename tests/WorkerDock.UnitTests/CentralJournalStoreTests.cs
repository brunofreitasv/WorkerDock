using System.Text;
using WorkerDock.Application.Common;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.UnitTests;

public sealed class CentralJournalStoreTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Reopen_preserves_acknowledged_records_and_sequences()
    {
        using var directory = new JournalDirectory();
        using (var store = CentralJournalStore.Open(directory.Path, InstanceId))
        {
            Assert.Equal(1, await store.AppendAsync(Mutation("Registered"), default));
            Assert.Equal(2, await store.AppendAsync(Mutation("Updated"), default));
        }

        using var reopened = CentralJournalStore.Open(directory.Path, InstanceId);
        Assert.Equal(new long[] { 1, 2 }, reopened.Records.Select(x => x.Sequence));
        Assert.Equal(new[] { "Registered", "Updated" }, reopened.Records.Select(x => x.Mutation.Action));
        Assert.True(reopened.IsWritable);
    }

    [Fact]
    public void Second_writer_is_rejected()
    {
        using var directory = new JournalDirectory();
        using var first = CentralJournalStore.Open(directory.Path, InstanceId);
        Assert.ThrowsAny<IOException>(() => CentralJournalStore.Open(directory.Path, InstanceId));
    }

    [Fact]
    public void Wrong_center_identity_is_rejected()
    {
        using var directory = new JournalDirectory();
        using (CentralJournalStore.Open(directory.Path, InstanceId)) { }
        Assert.Throws<InvalidDataException>(() => CentralJournalStore.Open(directory.Path, Guid.NewGuid()));
    }

    [Fact]
    public async Task Complete_corrupt_frame_is_rejected_without_repair()
    {
        using var directory = new JournalDirectory();
        using (var store = CentralJournalStore.Open(directory.Path, InstanceId))
            await store.AppendAsync(Mutation("Registered"), default);
        var path = System.IO.Path.Combine(directory.Path, "registry.journal");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => CentralJournalStore.Open(directory.Path, InstanceId));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Incomplete_final_frame_is_truncated_without_losing_acknowledged_record()
    {
        using var directory = new JournalDirectory();
        using (var store = CentralJournalStore.Open(directory.Path, InstanceId))
            await store.AppendAsync(Mutation("Registered"), default);
        var path = System.IO.Path.Combine(directory.Path, "registry.journal");
        var acknowledgedLength = new FileInfo(path).Length;
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            file.Write(new byte[] { 0x10, 0x00, 0x00, 0x00, 0x7B });

        using var reopened = CentralJournalStore.Open(directory.Path, InstanceId);
        Assert.Single(reopened.Records);
        Assert.Equal(acknowledgedLength, new FileInfo(path).Length);
        Assert.Equal(2, await reopened.AppendAsync(Mutation("Updated"), default));
    }

    [Fact]
    public async Task Oversize_payload_is_rejected_before_write()
    {
        using var directory = new JournalDirectory();
        using var store = CentralJournalStore.Open(directory.Path, InstanceId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await store.AppendAsync(Mutation("Registered") with { SafeDetailsJson = new string('x', 17_000) }, default));
        Assert.Empty(store.Records);
        Assert.True(store.IsWritable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_write_or_flush_failure_fences_store(bool completeWrite)
    {
        using var directory = new JournalDirectory();
        using var store = CentralJournalStore.Open(directory.Path, InstanceId, stream => new FailingWriter(stream, completeWrite));
        await Assert.ThrowsAsync<IOException>(async () => await store.AppendAsync(Mutation("Registered"), default));
        Assert.False(store.IsWritable);
        Assert.Empty(store.Records);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AppendAsync(Mutation("Updated"), default));
    }

    private static CentralJournalMutation Mutation(string action)
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var at = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var snapshot = new TenantRegistrationSnapshot(id, "Tenant", Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "https://tenant.example/", "HEALTH_REF", at, at);
        return new CentralJournalMutation(action, id, null, snapshot, at,
            new ActorContext("operator", "Entra", Guid.Parse("44444444-4444-4444-4444-444444444444")), "{}");
    }

    private sealed class FailingWriter(FileStream stream, bool completeWrite) : ICentralJournalWriter
    {
        public async ValueTask AppendAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            await stream.WriteAsync(completeWrite ? frame : frame[..Math.Min(5, frame.Length)], ct);
            throw new IOException(completeWrite ? "simulated flush failure" : "simulated partial write");
        }
    }

    private sealed class JournalDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workerdock-journal-" + Guid.NewGuid().ToString("N"));
        public JournalDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
