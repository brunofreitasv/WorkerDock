using WorkerDock.Application.Common;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;

namespace WorkerDock.Infrastructure.Scheduling;

public static class ScheduleInputConverter
{
    public static ScheduleDefinition ConvertInput(ScheduleEditorInput input)
    {
        if (input is null) throw new WorkerDockException("ValidationFailed", "schedule");
        if (string.IsNullOrWhiteSpace(input.ZoneId)) throw new WorkerDockException("ValidationFailed", "zoneId");
        try
        {
            DateTimeOffset? once = null;
            if (input.Kind == "Once")
            {
                if (input.Cron is not null || input.LocalOnce is null || input.LocalOnce.Value.Kind != DateTimeKind.Unspecified)
                    throw new WorkerDockException("ValidationFailed", "localOnce");
                once = new FiveFieldScheduleEvaluator().ResolveLocalOnce(input.LocalOnce.Value, input.ZoneId, input.SelectedOffset);
            }
            else if (input.Kind == "Cron")
            {
                if (input.LocalOnce is not null || input.SelectedOffset is not null || string.IsNullOrWhiteSpace(input.Cron))
                    throw new WorkerDockException("ValidationFailed", "cron");
            }
            else throw new WorkerDockException("ValidationFailed", "kind");
            return new ScheduleDefinition(Guid.NewGuid(), 1, input.Kind, input.Cron, input.ZoneId, once, DateTimeOffset.UtcNow);
        }
        catch (WorkerDockException error) when (error.Code is "InvalidTimeZone" or "NonexistentLocalTime" or "AmbiguousLocalTime" or "InvalidLocalOffset" or "InvalidLocalTime" or "InvalidLocalTimeKind")
        {
            var field = error.Code switch { "InvalidTimeZone" => "zoneId", "InvalidLocalOffset" => "selectedOffset", _ => "localOnce" };
            throw new WorkerDockException("ValidationFailed", field);
        }
        catch (TimeZoneNotFoundException) { throw new WorkerDockException("ValidationFailed", "zoneId"); }
        catch (InvalidTimeZoneException) { throw new WorkerDockException("ValidationFailed", "zoneId"); }
    }

    public static ScheduleEditorInput ToInput(ScheduleDefinition definition)
    {
        if (definition.Kind != "Once" || definition.OneTimeUtc is not { } utc)
            return new(definition.Kind, definition.Cron, definition.ZoneId, null, null);
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(definition.ZoneId));
        return new(definition.Kind, null, definition.ZoneId, DateTime.SpecifyKind(local.DateTime, DateTimeKind.Unspecified), local.Offset);
    }
}
