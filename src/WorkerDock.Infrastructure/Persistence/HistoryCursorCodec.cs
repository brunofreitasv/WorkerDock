using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkerDock.Application.Common;

namespace WorkerDock.Infrastructure.Persistence;

public sealed record HistoryPosition(DateTimeOffset Time, Guid Id, int Rank = 0);

public static class HistoryCursorCodec
{
    private const int MaxCursorLength = 2048;
    private const int Version = 1;

    public static string Encode(Guid instanceId, string scope, string filterKey, DateTimeOffset time, Guid id, int rank = 0)
    {
        if (instanceId == Guid.Empty || string.IsNullOrWhiteSpace(scope) || scope.Length > 100 ||
            filterKey is null || filterKey.Length > 4096 || time.Offset != TimeSpan.Zero || id == Guid.Empty || rank < 0)
            throw InvalidCursor();

        var payload = new CursorPayload(Version, instanceId, scope, Fingerprint(filterKey), time, id, rank);
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
        if (encoded.Length > MaxCursorLength) throw InvalidCursor();
        return encoded;
    }

    public static HistoryPosition? Decode(string? cursor, Guid instanceId, string scope, string filterKey)
    {
        if (cursor is null) return null;
        if (cursor.Length is 0 or > MaxCursorLength || instanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(scope) || scope.Length > 100 || filterKey is null || filterKey.Length > 4096)
            throw InvalidCursor();

        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(cursor));
            if (payload is null || payload.Version != Version || payload.InstanceId != instanceId ||
                !string.Equals(payload.Scope, scope, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(payload.FilterFingerprint),
                    Convert.FromHexString(Fingerprint(filterKey))) || payload.Time.Offset != TimeSpan.Zero ||
                payload.Id == Guid.Empty || payload.Rank < 0)
                throw InvalidCursor();

            return new HistoryPosition(payload.Time, payload.Id, payload.Rank);
        }
        catch (WorkerDockException) { throw; }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or CryptographicException)
        {
            throw InvalidCursor();
        }
    }

    private static string Fingerprint(string filterKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filterKey)));

    private static WorkerDockException InvalidCursor() => new("ValidationFailed", "cursor");

    private sealed record CursorPayload(int Version, Guid InstanceId, string Scope, string FilterFingerprint,
        DateTimeOffset Time, Guid Id, int Rank);
}
