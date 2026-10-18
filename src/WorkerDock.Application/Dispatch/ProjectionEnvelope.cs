using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorkerDock.Application.Dispatch;

public sealed record ProjectionEnvelope(Guid IntentId, string Kind, int SchemaVersion, Guid EntityId,
    long EntityRevision, string PayloadDigest, DateTimeOffset AvailableUtc, JsonElement Payload)
{
    public static string ExecutionDigest(Guid executionId, long revision, int ordinal = 1) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ExecutionDispatch|" + executionId.ToString("N") + "|" +
            revision.ToString(CultureInfo.InvariantCulture) + (ordinal == 1 ? "" : "|" + ordinal.ToString(CultureInfo.InvariantCulture)))));
}
