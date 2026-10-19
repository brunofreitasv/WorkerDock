using System.Globalization;
using System.Text.Json;
using WorkerDock.Contracts.Logging;
namespace WorkerDock.Infrastructure.Logging;

/// <summary>Fixed event/field vocabulary; handler text and provider exceptions cannot become diagnostics.</summary>
public sealed class SafeExecutionLog(Action<string> emit) : ISafeExecutionLog
{
    private static readonly HashSet<string> Events = new(StringComparer.Ordinal) { "TemplateStarted", "TemplateCompleted", "TemplateFailed", "SecretUnavailable" };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal) { "Success", "SecretUnavailable", "TemplateCancelled", "TemplateExecutionFailed", "TemplateConfigurationInvalid", "ConnectivityFailed", "AuthenticationFailed", "DestinationUnavailable", "CommandFailed", "Timeout" };
    private const int MaximumEvents = 256;
    private int accepted;
    private int markerEmitted;
    private long dropped;
    public long DroppedCount => Interlocked.Read(ref dropped);

    public ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string,string> safeFields, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) { Drop(); return ValueTask.CompletedTask; }
        if (!Events.Contains(eventCode) || safeFields is null || safeFields.Count > 8 || Interlocked.Increment(ref accepted) > MaximumEvents)
        { Drop(); return ValueTask.CompletedTask; }
        var projected = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var field in safeFields)
        {
            var valid = field.Value is { Length: > 0 and <= 80 } && field.Key switch
            {
                "executionId" or "attemptId" or "correlationId" => Guid.TryParseExact(field.Value,"D",out _),
                "durationMs" or "affectedRows" => long.TryParse(field.Value,NumberStyles.None,CultureInfo.InvariantCulture,out var n) && n >= 0,
                "outcome" => field.Value is "Success" or "TransientFailure" or "PermanentFailure",
                "safeCode" => Codes.Contains(field.Value),
                _ => false
            };
            if (!valid) { Drop(); return ValueTask.CompletedTask; }
            projected.Add(field.Key,field.Value);
        }
        Emit(JsonSerializer.Serialize(new { eventCode, safeFields = projected }));
        return ValueTask.CompletedTask;
    }

    private void Drop()
    {
        var count = Interlocked.Increment(ref dropped);
        if (Interlocked.Exchange(ref markerEmitted, 1) == 0)
            Emit(JsonSerializer.Serialize(new { eventCode = "DiagnosticsDropped", safeFields = new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) } }));
    }

    private void Emit(string record)
    {
        try { emit(record); }
        catch { Interlocked.Increment(ref dropped); }
    }
}
