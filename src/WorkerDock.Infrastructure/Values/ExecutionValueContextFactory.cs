using WorkerDock.Contracts.Values;

namespace WorkerDock.Infrastructure.Values;

public static class ExecutionValueContextFactory
{
    public static ExecutionValueContext Scheduled(DynamicValuePlan plan, DateTimeOffset scheduledForUtc)
    {
        var frozen = DynamicValueJson.Freeze(plan);
        if (scheduledForUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Scheduled time must be UTC.", nameof(scheduledForUtc));
        return new(1, frozen.TimeZoneId, "Scheduled", scheduledForUtc, null, null, null);
    }

    public static ExecutionValueContext Manual(DynamicValuePlan plan, DateTimeOffset acceptedAtUtc, DateOnly? referenceDateOverride)
    {
        var frozen = DynamicValueJson.Freeze(plan);
        if (acceptedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Accepted time must be UTC.", nameof(acceptedAtUtc));
        return new(1, frozen.TimeZoneId, "Manual", null, acceptedAtUtc, referenceDateOverride, null);
    }
}
