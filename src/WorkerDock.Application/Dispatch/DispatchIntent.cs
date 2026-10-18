using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WorkerDock.Application.Dispatch;

public sealed record DispatchIntent(Guid DeliveryId, Guid ExecutionId, int AttemptOrdinal, string TemplateId,
    DateTimeOffset DueUtc, int Priority, long EntityRevision, string PayloadDigest, long CapacityRevision = 0)
{
    public static Guid CapacityDeliveryId(Guid executionId, int ordinal, long capacityRevision)
    {
        var identity = string.Join("/", "capacity", executionId.ToString("N"), ordinal.ToString(CultureInfo.InvariantCulture), capacityRevision.ToString(CultureInfo.InvariantCulture));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
    public string ComputeDigest() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", "ExecutionDispatch/v1", ExecutionId.ToString("N"), DeliveryId.ToString("N"),
            AttemptOrdinal.ToString(CultureInfo.InvariantCulture), TemplateId,
            EntityRevision.ToString(CultureInfo.InvariantCulture), DueUtc.UtcTicks.ToString(CultureInfo.InvariantCulture), CapacityRevision.ToString(CultureInfo.InvariantCulture)))));
}
