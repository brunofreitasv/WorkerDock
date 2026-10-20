using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkerDock.ControlCenter.Registry;

public sealed class CentralJournalStore : IDisposable
{
    private const int MaxPayloadBytes = 16 * 1024;
    private const int DigestBytes = 32;
    private const int HeaderBytes = 24;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WDJNL001");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FileStream _stream;
    private readonly ICentralJournalWriter _writer;
    private readonly Guid _instanceId;
    private readonly List<CentralJournalRecord> _records = [];
    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private byte[] _previousHash = new byte[DigestBytes];
    private bool _disposed;

    private CentralJournalStore(FileStream stream, Guid instanceId, Func<FileStream, ICentralJournalWriter>? writerFactory)
    {
        _stream = stream;
        _instanceId = instanceId;
        _writer = writerFactory is null ? new FileJournalWriter(stream) : writerFactory(stream);
    }

    public IReadOnlyList<CentralJournalRecord> Records => _records.AsReadOnly();
    public bool IsWritable { get; private set; } = true;

    public static CentralJournalStore Open(string dataDirectory, Guid instanceId) => Open(dataDirectory, instanceId, null);

    internal static CentralJournalStore Open(string dataDirectory, Guid instanceId, Func<FileStream, ICentralJournalWriter>? writerFactory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Data directory is required.", nameof(dataDirectory));
        if (instanceId == Guid.Empty) throw new ArgumentException("Center instance ID is required.", nameof(instanceId));
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "registry.journal");
        var isNew = !File.Exists(path);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            4096, FileOptions.WriteThrough);
        try
        {
            var store = new CentralJournalStore(stream, instanceId, writerFactory);
            if (isNew) store.WriteHeader();
            else store.Replay();
            return store;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask<long> AppendAsync(CentralJournalMutation mutation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateMutation(mutation);
        await _appendLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsWritable) throw new InvalidOperationException("Journal is not writable.");
            var sequence = _records.Count == 0 ? 1 : checked(_records[^1].Sequence + 1);
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new FramePayload(_instanceId, sequence, Convert.ToHexString(_previousHash), mutation), JsonOptions);
            if (payload.Length > MaxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(mutation), "Journal payload exceeds 16 KiB.");
            var hash = SHA256.HashData(payload);
            var frame = new byte[sizeof(int) + payload.Length + DigestBytes];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame.AsSpan(sizeof(int)));
            hash.CopyTo(frame.AsSpan(sizeof(int) + payload.Length));
            try
            {
                await _writer.AppendAndFlushAsync(frame, ct);
            }
            catch
            {
                IsWritable = false;
                throw;
            }
            _previousHash = hash;
            _records.Add(new CentralJournalRecord(sequence, mutation));
            return sequence;
        }
        finally
        {
            _appendLock.Release();
        }
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        Magic.CopyTo(header);
        _instanceId.TryWriteBytes(header[Magic.Length..]);
        _stream.Write(header);
        _stream.Flush(true);
    }

    private void Replay()
    {
        if (_stream.Length < HeaderBytes) throw new InvalidDataException("Journal identity header is incomplete.");
        Span<byte> header = stackalloc byte[HeaderBytes];
        _stream.ReadExactly(header);
        if (!header[..Magic.Length].SequenceEqual(Magic) ||
            new Guid(header[Magic.Length..]) != _instanceId)
            throw new InvalidDataException("Journal format or center identity does not match.");

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        while (_stream.Position < _stream.Length)
        {
            var frameStart = _stream.Position;
            if (_stream.Length - frameStart < sizeof(int)) { RecoverTail(frameStart); break; }
            _stream.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 1 or > MaxPayloadBytes)
                throw new InvalidDataException("Journal frame length is invalid.");
            if (_stream.Length - _stream.Position < length + DigestBytes) { RecoverTail(frameStart); break; }
            var payload = new byte[length];
            _stream.ReadExactly(payload);
            var storedHash = new byte[DigestBytes];
            _stream.ReadExactly(storedHash);
            var actualHash = SHA256.HashData(payload);
            if (!CryptographicOperations.FixedTimeEquals(storedHash, actualHash))
                throw new InvalidDataException("Journal frame checksum failed.");
            FramePayload envelope;
            try { envelope = JsonSerializer.Deserialize<FramePayload>(payload, JsonOptions)
                ?? throw new JsonException("Empty journal frame."); }
            catch (JsonException ex) { throw new InvalidDataException("Journal frame payload is invalid.", ex); }
            if (envelope.InstanceId != _instanceId ||
                envelope.Sequence != (_records.Count == 0 ? 1 : _records[^1].Sequence + 1) ||
                !string.Equals(envelope.PreviousHash, Convert.ToHexString(_previousHash), StringComparison.Ordinal) ||
                envelope.Mutation is null)
                throw new InvalidDataException("Journal frame sequence, chain, or identity failed.");
            try { ValidateMutation(envelope.Mutation); }
            catch (ArgumentException ex) { throw new InvalidDataException("Journal mutation is invalid.", ex); }
            _records.Add(new CentralJournalRecord(envelope.Sequence, envelope.Mutation));
            _previousHash = actualHash;
        }
        _stream.Position = _stream.Length;
    }

    private void RecoverTail(long start)
    {
        _stream.SetLength(start);
        _stream.Flush(true);
        _stream.Position = start;
    }

    private static void ValidateMutation(CentralJournalMutation mutation)
    {
        if (mutation.Action is not ("Registered" or "Updated" or "Removed" or "ImportedRegistration" or "LegacyAudit"))
            throw new ArgumentException("Journal action is invalid.", nameof(mutation));
        if (mutation.RegistrationId == Guid.Empty || mutation.Actor is null ||
            string.IsNullOrWhiteSpace(mutation.Actor.Subject) ||
            string.IsNullOrWhiteSpace(mutation.Actor.AuthenticationMode) ||
            mutation.Actor.CorrelationId == Guid.Empty || mutation.SafeDetailsJson is null ||
            mutation.AtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Journal mutation metadata is invalid.", nameof(mutation));
        if ((mutation.Before is not null && mutation.Before.Id != mutation.RegistrationId) ||
            (mutation.After is not null && mutation.After.Id != mutation.RegistrationId))
            throw new ArgumentException("Journal registration identity is invalid.", nameof(mutation));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsWritable = false;
        _stream.Dispose();
    }

    private sealed record FramePayload(Guid InstanceId, long Sequence, string PreviousHash, CentralJournalMutation Mutation);

    private sealed class FileJournalWriter(FileStream stream) : ICentralJournalWriter
    {
        public async ValueTask AppendAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            await stream.WriteAsync(frame, ct);
            stream.Flush(true);
        }
    }
}
