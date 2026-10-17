using WorkerDock.Domain.Scheduling;
namespace WorkerDock.Application.Scheduling;
public interface IScheduleEvaluator
{
    IReadOnlyList<string> Validate(ScheduleDefinition schedule);
    IReadOnlyList<DateTimeOffset> Next(ScheduleDefinition schedule, DateTimeOffset afterExclusiveUtc, int count);
    DateTimeOffset ResolveLocalOnce(DateTime localUnspecified, string zoneId, TimeSpan? selectedOffset);
}
