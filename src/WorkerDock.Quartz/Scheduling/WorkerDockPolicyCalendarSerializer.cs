using System.Text.Json;
using global::Quartz.Serialization.SystemTextJson.Calendars;
using WorkerDock.Application.Common;

namespace WorkerDock.Quartz.Scheduling;

public sealed class WorkerDockPolicyCalendarSerializer : CalendarSerializer<WorkerDockPolicyCalendar>
{
    public override string CalendarTypeName => "WorkerDock.PolicyCalendar.v1";

    protected override WorkerDockPolicyCalendar Create(JsonElement json, JsonSerializerOptions options)
    {
        if (!json.TryGetProperty("SchemaVersion", out var version) || !version.TryGetInt32(out var value) ||
            value != WorkerDockPolicyCalendar.SchemaVersion ||
            !json.TryGetProperty("Policy", out var policy) || policy.GetString() != WorkerDockPolicyCalendar.Policy)
            throw new WorkerDockException("UnsupportedCalendarVersion");
        return new(json.GetProperty("Expression").GetString()!, json.GetProperty("ZoneId").GetString()!);
    }

    protected override void SerializeFields(Utf8JsonWriter writer, WorkerDockPolicyCalendar calendar, JsonSerializerOptions options)
    {
        writer.WriteNumber("SchemaVersion", WorkerDockPolicyCalendar.SchemaVersion);
        writer.WriteString("Expression", calendar.Expression);
        writer.WriteString("ZoneId", calendar.ZoneId);
        writer.WriteString("Policy", WorkerDockPolicyCalendar.Policy);
    }

    protected override void DeserializeFields(WorkerDockPolicyCalendar calendar, JsonElement json, JsonSerializerOptions options)
    {
        // Immutable policy fields were validated and restored by Create.
    }
}
