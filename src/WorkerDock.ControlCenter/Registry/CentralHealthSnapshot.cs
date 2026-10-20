using System.Text.Json;
using WorkerDock.Application.Common;

namespace WorkerDock.ControlCenter.Registry;

public sealed record HealthObservation(string Health, string? SafeCode, DateTimeOffset LastAttemptUtc, DateTimeOffset? LastSuccessUtc);

public sealed class CentralHealthSnapshot : IDisposable
{
    private const long MaximumSnapshotBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly Dictionary<Guid, HealthObservation> _observations;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask> _persist;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private bool _disposed;

    private CentralHealthSnapshot(string path, Dictionary<Guid, HealthObservation> observations,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask> persist)
    {
        _path = path;
        _observations = observations;
        _persist = persist;
    }

    public static CentralHealthSnapshot Open(string dataDirectory)
        => Open(dataDirectory, null);

    internal static CentralHealthSnapshot Open(string dataDirectory,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? persist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "health.json");
        var persistOperation = persist ?? PersistFileAsync;
        if (!File.Exists(path)) return new CentralHealthSnapshot(path, [], persistOperation);

        try
        {
            if (new FileInfo(path).Length > MaximumSnapshotBytes) throw new InvalidDataException("Health snapshot exceeds the supported size.");
            var observations = JsonSerializer.Deserialize<Dictionary<Guid, HealthObservation>>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new JsonException("Empty health snapshot.");
            foreach (var (id, value) in observations.ToArray())
            {
                if (id == Guid.Empty || value is null || !IsHealth(value.Health) || value.LastAttemptUtc.Offset != TimeSpan.Zero ||
                    value.LastSuccessUtc?.Offset != TimeSpan.Zero && value.LastSuccessUtc is not null)
                    throw new JsonException("Invalid health observation.");
                observations[id] = value with { SafeCode = SafeCode(value.SafeCode) };
            }
            return new CentralHealthSnapshot(path, observations, persistOperation);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning("Control Center health snapshot is unavailable or invalid; health will start as Unknown.");
            return new CentralHealthSnapshot(path, [], persistOperation);
        }
    }

    public HealthObservation? Get(Guid id)
    {
        lock (_sync) return _observations.TryGetValue(id, out var value) ? value : null;
    }

    public async ValueTask SaveAsync(Guid id, HealthObservation value, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        if (id == Guid.Empty || !IsHealth(value.Health) || value.LastAttemptUtc.Offset != TimeSpan.Zero ||
            value.LastSuccessUtc?.Offset != TimeSpan.Zero && value.LastSuccessUtc is not null)
            throw new WorkerDockException("ValidationFailed");

        await _gate.WaitAsync(ct);
        try
        {
            Dictionary<Guid, HealthObservation> updated;
            lock (_sync) updated = new Dictionary<Guid, HealthObservation>(_observations);
            updated[id] = value with { SafeCode = SafeCode(value.SafeCode) };
            await PersistAsync(updated, ct);
            lock (_sync)
            {
                _observations.Clear();
                foreach (var item in updated) _observations.Add(item.Key, item.Value);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RemoveAsync(Guid id, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            Dictionary<Guid, HealthObservation> updated;
            lock (_sync)
            {
                if (!_observations.ContainsKey(id)) return;
                updated = new Dictionary<Guid, HealthObservation>(_observations);
            }
            updated.Remove(id);
            await PersistAsync(updated, ct);
            lock (_sync)
            {
                _observations.Clear();
                foreach (var item in updated) _observations.Add(item.Key, item.Value);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(Dictionary<Guid, HealthObservation> observations, CancellationToken ct)
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(observations, JsonOptions);
            if (bytes.LongLength > MaximumSnapshotBytes) throw new InvalidDataException("Health snapshot exceeds the supported size.");
            await _persist(temporaryPath, bytes, ct);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsHealth(string health) => health is "Healthy" or "Unhealthy" or "IdentityMismatch" or "Unknown";
    private static string? SafeCode(string? value) => value is not null && value.Length <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;

    private static async ValueTask PersistFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        stream.Flush(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
