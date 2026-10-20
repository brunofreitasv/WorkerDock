namespace WorkerDock.Contracts.Values;

public sealed record ExecutionValueContext(int ResolverVersion, string TimeZoneId, string TriggerKind,
    DateTimeOffset? ScheduledForUtc, DateTimeOffset? RequestedAtUtc,
    DateOnly? ReferenceDateOverride, DateTimeOffset? FirstStartedAtUtc);
