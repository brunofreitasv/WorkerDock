using System.Diagnostics;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Application.Observability;

public static class ExecutionActivitySource
{
    public const string InstrumentationName = "WorkerDock";
    public static readonly ActivitySource Source = new(InstrumentationName);

    public static Activity? StartAttempt(AttemptLease lease, ExecutionDescriptor descriptor)
    {
        try
        {
            var activity = Source.StartActivity("workerdock.execution", ActivityKind.Internal);
            if (activity is null) return null;
            activity.SetTag("execution.id", lease.ExecutionId.ToString("D"));
            activity.SetTag("attempt.id", lease.AttemptId.ToString("D"));
            activity.SetTag("attempt.ordinal", lease.Ordinal);
            activity.SetTag("worker.generation", lease.Generation);
            activity.SetTag("template.id", descriptor.TemplateId);
            activity.SetTag("template.version", descriptor.TemplateVersion);
            return activity;
        }
        catch { return null; }
    }

    public static void StopAttempt(Activity? activity, string localOutcome, TemplateResult? result,
        bool exitConfirmed, bool effectsMayHaveOccurred, bool completionPersisted)
    {
        if (activity is null) return;
        try
        {
            var outcome = localOutcome switch
            {
                "Completed" when result?.Outcome == "Success" && exitConfirmed && completionPersisted => "Succeeded",
                "Completed" => "Failed",
                "TimedOut" => "TimedOut",
                "Stopped" => "Stopped",
                "ProcessFailed" => "Failed",
                _ => "Unknown"
            };
            activity.SetTag("outcome", outcome);
            activity.SetTag("process.exit_confirmed", exitConfirmed);
            activity.SetTag("execution.effects_may_have_occurred", effectsMayHaveOccurred);
            activity.SetTag("completion.persisted", completionPersisted);
            if (result?.Outcome is "Success" or "TransientFailure" or "PermanentFailure")
                activity.SetTag("handler.outcome", result.Outcome);
            if (IsSafeCode(result?.SafeCode)) activity.SetTag("handler.safe_code", result!.SafeCode);
            activity.SetStatus(outcome == "Succeeded" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }
        catch { }
        finally { try { activity.Stop(); } catch { } }
    }

    private static bool IsSafeCode(string? value) => value is { Length: > 0 and <= 80 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
