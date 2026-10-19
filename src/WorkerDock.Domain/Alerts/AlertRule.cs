namespace WorkerDock.Domain.Alerts;

public sealed record AlertRule(
    Guid Id,
    string Name,
    string Condition,
    int Threshold,
    TimeSpan Window,
    TimeSpan Grace,
    TimeSpan ReminderInterval,
    Guid SinkId,
    bool Enabled);

public static class AlertRuleValidation
{
    public static readonly IReadOnlySet<string> Conditions = new HashSet<string>(StringComparer.Ordinal)
    {
        "RepeatedFailure", "RetryExhausted", "Timeout", "LongRunning", "MissedExecution", "WorkerUnavailable"
    };

    public static void Validate(AlertRule rule)
    {
        if (rule is null || rule.Id == Guid.Empty || rule.SinkId == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 150 ||
            rule.Name.Any(char.IsControl) || !Conditions.Contains(rule.Condition) || rule.Threshold is < 1 or > 1000 ||
            rule.Window < TimeSpan.FromSeconds(1) || rule.Window > TimeSpan.FromDays(30) ||
            rule.Grace < TimeSpan.Zero || rule.Grace > TimeSpan.FromDays(30) ||
            rule.ReminderInterval < TimeSpan.Zero || rule.ReminderInterval > TimeSpan.FromDays(30) ||
            rule.Window.Ticks % TimeSpan.TicksPerSecond != 0 || rule.Grace.Ticks % TimeSpan.TicksPerSecond != 0 ||
            rule.ReminderInterval.Ticks % TimeSpan.TicksPerSecond != 0 ||
            rule.Condition is "LongRunning" or "WorkerUnavailable" && rule.Threshold != 1)
            throw new ArgumentException("Alert rule is invalid.", nameof(rule));
    }
}
