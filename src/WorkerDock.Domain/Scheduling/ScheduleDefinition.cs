namespace WorkerDock.Domain.Scheduling;
public sealed record ScheduleDefinition(Guid ScheduleId, long Revision, string Kind, string? Cron,
    string ZoneId, DateTimeOffset? OneTimeUtc, DateTimeOffset ActivatedUtc)
{
    public const string Dialect = "WorkerDockFiveFieldV1";
}
