namespace WorkerDock.Application.Jobs;

public sealed record ManualRunOptions(DateOnly? ReferenceDateOverride = null);
