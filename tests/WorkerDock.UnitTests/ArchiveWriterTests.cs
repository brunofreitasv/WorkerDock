using System.Text;
using WorkerDock.Application.Retention;
using WorkerDock.Infrastructure.Retention;

namespace WorkerDock.UnitTests;

public sealed class ArchiveWriterTests
{
    [Fact]
    public async Task Partial_source_write_leaves_no_final_or_temporary_archive()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);

        await Assert.ThrowsAsync<IOException>(async () => await writer.WriteAsync(Guid.NewGuid(), new FailingReadStream(), CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Finalize_failure_leaves_no_temporary_archive()
    {
        using var directory = new TemporaryDirectory();
        var batchId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(directory.Path, batchId.ToString("N") + ".archive.gz"));
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);

        await Assert.ThrowsAnyAsync<IOException>(async () => await writer.WriteAsync(batchId, Record("one"), CancellationToken.None));

        Assert.Single(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Verification_rejects_archive_whose_checksum_changed()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);
        var manifest = await writer.WriteAsync(Guid.NewGuid(), Record("one\ntwo"), CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(directory.Path, manifest.RelativePath), "corrupt");

        Assert.False(await writer.VerifyAsync(manifest, CancellationToken.None));
    }

    [Fact]
    public async Task Verification_rejects_paths_outside_root()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);
        var manifest = new ArchiveManifest(Guid.NewGuid(), "..\\outside.archive.gz", new string('0', 64), 0, DateTimeOffset.UtcNow);

        Assert.False(await writer.VerifyAsync(manifest, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_only_removes_archives_contained_by_the_configured_root()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);
        var manifest = await writer.WriteAsync(Guid.NewGuid(), Record("one"), CancellationToken.None);

        Assert.True(await writer.DeleteAsync(manifest.RelativePath, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory.Path, manifest.RelativePath)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.DeleteAsync("../outside.archive.gz", CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_input_is_rejected_without_publishing_partial_archive()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 4);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await writer.WriteAsync(Guid.NewGuid(), Record("12345"), CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Repeating_batch_id_returns_the_single_finalized_archive()
    {
        using var directory = new TemporaryDirectory();
        var batchId = Guid.NewGuid();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);
        var first = await writer.WriteAsync(batchId, Record("original"), CancellationToken.None);
        var retry = await writer.WriteAsync(batchId, Record("original"), CancellationToken.None);

        Assert.Equal(first.RelativePath, retry.RelativePath);
        Assert.Equal(first.Sha256, retry.Sha256);
        Assert.Equal(1, retry.RowCount);
        Assert.Single(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task Reusing_batch_id_with_different_records_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var batchId = Guid.NewGuid();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);
        await writer.WriteAsync(batchId, Record("original"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await writer.WriteAsync(batchId, Record("different"), CancellationToken.None));
    }

    [Fact]
    public async Task Write_counts_records_and_verifies_finalized_archive()
    {
        using var directory = new TemporaryDirectory();
        var writer = new ArchiveWriter(directory.Path, maxBytes: 1024);
        var manifest = await writer.WriteAsync(Guid.NewGuid(), Record("one\ntwo\n"), CancellationToken.None);

        Assert.Equal(2, manifest.RowCount);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.True(await writer.VerifyAsync(manifest, CancellationToken.None));
    }

    private static MemoryStream Record(string text) => new(Encoding.UTF8.GetBytes(text));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkerDockArchiveTests_" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FailingReadStream : MemoryStream
    {
        private int reads;
        public FailingReadStream() : base(Encoding.UTF8.GetBytes("partial-record\nmore")) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (reads++ > 0) throw new IOException("simulated source read failure");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
