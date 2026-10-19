using System.IO.Compression;
using System.Security.Cryptography;
using WorkerDock.Application.Retention;

namespace WorkerDock.Infrastructure.Retention;

/// <summary>Writes bounded, compressed archive batches beneath a deployment-owned root.</summary>
public sealed class ArchiveWriter
{
    private readonly string root;
    private readonly StringComparison pathComparison;
    private readonly long maxBytes;

    public ArchiveWriter(string root, long maxBytes = 268_435_456)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (maxBytes is < 1 or > 4_294_967_296) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.root = Path.GetFullPath(root);
        this.maxBytes = maxBytes;
        pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Directory.CreateDirectory(this.root);
        EnsureNoReparsePoints(this.root);
    }

    public async ValueTask<ArchiveManifest> WriteAsync(Guid batchId, Stream safeRecords, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(safeRecords);
        if (!safeRecords.CanRead) throw new ArgumentException("Archive records must be readable.", nameof(safeRecords));
        EnsureNoReparsePoints(root);
        var relativePath = batchId.ToString("N") + ".archive.gz";
        var finalPath = ResolveContainedPath(relativePath);
        if (File.Exists(finalPath)) return await ManifestForExistingAsync(batchId, relativePath, finalPath, safeRecords, ct);

        var temporaryPath = Path.Combine(root, "." + batchId.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".tmp");
        long rowCount = 0;
        long bytesRead = 0;
        var lastByte = -1;
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = await safeRecords.ReadAsync(buffer, ct);
                        if (read == 0) break;
                        bytesRead = checked(bytesRead + read);
                        if (bytesRead > maxBytes) throw new InvalidDataException("Archive batch exceeds the configured size limit.");
                        for (var i = 0; i < read; i++)
                        {
                            if (buffer[i] == (byte)'\n') rowCount++;
                            lastByte = buffer[i];
                        }
                        await gzip.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (bytesRead > 0 && lastByte != (byte)'\n') rowCount++;
                }
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            EnsureNoReparsePoints(root);
            File.Move(temporaryPath, finalPath, overwrite: false);
            return await CreateManifestAsync(batchId, relativePath, finalPath, rowCount, ct);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async ValueTask<bool> VerifyAsync(ArchiveManifest manifest, CancellationToken ct)
    {
        if (manifest is null || manifest.BatchId == Guid.Empty || manifest.RowCount < 0 ||
            manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)) return false;
        try
        {
            var path = ResolveContainedPath(manifest.RelativePath);
            EnsureNoReparsePoints(path);
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length > maxBytes + 1_048_576) return false;
            await using var archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archive, ct));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(manifest.Sha256), Convert.FromHexString(actualHash))) return false;
            return await CountRowsAsync(path, maxBytes, ct) == manifest.RowCount;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or FormatException)
        {
            return false;
        }
    }

    public ValueTask<bool> DeleteAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolveContainedPath(relativePath);
        EnsureNoReparsePoints(path);
        if (!File.Exists(path)) return ValueTask.FromResult(false);
        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    private async ValueTask<ArchiveManifest> ManifestForExistingAsync(Guid batchId, string relativePath, string path, Stream source, CancellationToken ct)
    {
        EnsureNoReparsePoints(path);
        var rowCount = await CompareSourceAsync(path, source, maxBytes, ct);
        return await CreateManifestAsync(batchId, relativePath, path, rowCount, ct);
    }

    private static async ValueTask<long> CompareSourceAsync(string path, Stream source, long maxBytes, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var expected = new byte[64 * 1024];
        var actual = new byte[64 * 1024];
        long bytes = 0;
        long rows = 0;
        var lastByte = -1;
        while (true)
        {
            var expectedRead = await source.ReadAsync(expected, ct);
            var actualRead = await gzip.ReadAsync(actual, ct);
            if (expectedRead != actualRead || !expected.AsSpan(0, expectedRead).SequenceEqual(actual.AsSpan(0, actualRead)))
                throw new InvalidDataException("Archive batch identifier was reused for different records.");
            if (expectedRead == 0) break;
            bytes = checked(bytes + expectedRead);
            if (bytes > maxBytes) throw new InvalidDataException("Archive batch exceeds the configured size limit.");
            for (var i = 0; i < expectedRead; i++)
            {
                if (expected[i] == (byte)'\n') rows++;
                lastByte = expected[i];
            }
        }
        return bytes > 0 && lastByte != (byte)'\n' ? rows + 1 : rows;
    }

    private async ValueTask<ArchiveManifest> CreateManifestAsync(Guid batchId, string relativePath, string path, long rowCount, CancellationToken ct)
    {
        EnsureNoReparsePoints(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        return new ArchiveManifest(batchId, relativePath, hash, rowCount, DateTimeOffset.UtcNow);
    }

    private static async ValueTask<long> CountRowsAsync(string path, long maxBytes, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var buffer = new byte[64 * 1024];
        long rows = 0;
        long bytes = 0;
        var lastByte = -1;
        while (true)
        {
            var read = await gzip.ReadAsync(buffer, ct);
            if (read == 0) break;
            bytes = checked(bytes + read);
            if (bytes > maxBytes) throw new InvalidDataException("Archive batch exceeds the configured size limit.");
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') rows++;
                lastByte = buffer[i];
            }
        }
        return bytes > 0 && lastByte != (byte)'\n' ? rows + 1 : rows;
    }

    private string ResolveContainedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new ArgumentException("Archive path is invalid.", nameof(relativePath));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, pathComparison)) throw new ArgumentException("Archive path escapes its configured root.", nameof(relativePath));
        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Archive path contains a reparse point.");
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = parent ?? string.Empty;
        }
    }
}
