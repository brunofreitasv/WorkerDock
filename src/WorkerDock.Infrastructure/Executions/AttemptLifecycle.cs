using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Execution;
using WorkerDock.Domain.Executions;
using WorkerDock.Domain.Jobs;

namespace WorkerDock.Infrastructure.Executions;

// One transaction's data. SQL supplies its own clock and persists changes under the tenant business lock.
internal sealed class AttemptLifecycle
{
    public required ExecutionDescriptor Descriptor { get; init; }
    public required ExecutionPolicy Policy { get; init; }
    public required DispatchIntent Intent { get; set; }
    public required Guid JobId { get; init; }
    public string State { get; set; } = "Queued";
    public bool PermitRetained { get; set; } = true;
    public bool StopRequested { get; set; }
    public bool Force { get; set; }
    public long EffectiveCapacityRevision { get; set; }
    public bool GateOpen { get; set; } = true;
    public bool DescriptorAvailable { get; set; } = true;
    public bool Uncertain { get; set; }
    public AttemptLease? Lease { get; set; }
    public string? AttemptState { get; set; }
    public bool LocalExitConfirmed { get; set; }
    public string? SafeCode { get; set; }
    public List<string> Events { get; } = [];
    public DispatchIntent? NewDispatch { get; private set; }
    public int Ordinal => Lease?.Ordinal ?? 0;
    private static bool Terminal(string state) => state is "Succeeded" or "Failed" or "Stopped" or "TimedOut" or "Skipped";

    public AttemptStart Begin(DispatchIntent intent, string boot, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(boot) || boot.Length > 100 || intent.PayloadDigest != intent.ComputeDigest() ||
            intent.ExecutionId != Descriptor.ExecutionId || intent.EntityRevision != Intent.EntityRevision || intent.TemplateId != Descriptor.TemplateId)
            throw new WorkerDockException("DispatchIntegrityError");
        if (Lease is { } existing && intent.AttemptOrdinal <= existing.Ordinal)
        {
            if (intent.AttemptOrdinal < existing.Ordinal || AttemptState != "Running") return new("Completed", null);
            if (existing.ExpiresUtc > now) return new("AlreadyOwned", null);
            Review(now);
            return new(State == "NeedsReview" ? "NeedsReview" : "Completed", null);
        }
        if (Terminal(State)) return new("Completed", null);
        if (State == "NeedsReview") return new("NeedsReview", null);
        if (intent.CapacityRevision != EffectiveCapacityRevision) return new("Held", null);
        if (intent with { Priority = Intent.Priority } != Intent || intent.AttemptOrdinal != Ordinal + 1) throw new WorkerDockException("DispatchIntegrityError");
        if (!GateOpen || StopRequested || !DescriptorAvailable || intent.DueUtc > now || State is not ("Queued" or "RetryWaiting")) return new("Held", null);
        Lease = new(Guid.NewGuid(), intent.ExecutionId, intent.AttemptOrdinal, (Lease?.Generation ?? 0) + 1,
            boot, now + OwnershipDefaults.LeaseDuration, Policy.Timeout is { } timeout ? now + timeout : null);
        State = "Running"; AttemptState = "Running"; LocalExitConfirmed = false; SafeCode = null;
        Events.Add("Running");
        return new("Started", Lease);
    }

    public bool Owned(AttemptLease lease, DateTimeOffset now) => AttemptState == "Running" && Lease is { } current &&
        current.AttemptId == lease.AttemptId && current.ExecutionId == lease.ExecutionId && current.Ordinal == lease.Ordinal &&
        current.Generation == lease.Generation && current.WorkerBootId == lease.WorkerBootId && current.ExpiresUtc > now;

    public ExecutionDescriptor GetDescriptor(AttemptLease lease, DateTimeOffset now)
    {
        if (!Owned(lease, now)) throw new WorkerDockException("AttemptOwnershipLost");
        if (!DescriptorAvailable) throw new WorkerDockException("PinnedDescriptorUnavailable");
        if (StopRequested) throw new WorkerDockException("AttemptStartHeld");
        return Descriptor with { AttemptOrdinal = lease.Ordinal, DeadlineUtc = Lease!.DeadlineUtc };
    }
    public AttemptLease? Renew(AttemptLease lease, DateTimeOffset now)
    {
        if (!Owned(lease, now)) return null;
        return Lease = Lease! with { ExpiresUtc = now + OwnershipDefaults.LeaseDuration };
    }
    public void Complete(AttemptLease lease, AttemptCompletion completion, DateTimeOffset now)
    {
        if (!Owned(lease, now)) return;
        if (completion.LocalOutcome is not ("Completed" or "TimedOut" or "Stopped" or "ProcessFailed"))
            throw new WorkerDockException("InvalidAttemptOutcome");
        LocalExitConfirmed = completion.LocalExitConfirmed;
        Uncertain = completion.EffectsMayHaveOccurred || completion.HandlerResult?.EffectsMayHaveOccurred == true;
        SafeCode = completion.HandlerResult?.SafeCode;
        var result = completion.HandlerResult;
        AttemptState = !LocalExitConfirmed ? "Abandoned" : StopRequested || completion.LocalOutcome == "Stopped" ? "Stopped" :
            completion.LocalOutcome == "Completed" ? result?.Outcome == "Success" ? "Succeeded" : "Failed" : completion.LocalOutcome;
        if (!LocalExitConfirmed) Uncertain = true;
        if (LocalExitConfirmed && (StopRequested || completion.LocalOutcome == "Stopped")) { Finish("Stopped"); return; }
        if (!LocalExitConfirmed || (result?.Outcome != "Success" && Uncertain && completion.LocalOutcome != "TimedOut"))
        { DecideUnknown(now); return; }
        if (completion.LocalOutcome == "TimedOut") Finish("TimedOut");
        else if (result?.Outcome == "Success") { Uncertain = false; Finish("Succeeded"); }
        else
        {
            var retry = RetryDecision.Decide(Policy, Ordinal, result?.Outcome, result?.SafeCode, false, false, result?.RetryAfter, Random.Shared.NextDouble());
            if (retry.Retry) ScheduleRetry(now + retry.Delay); else Finish("Failed");
        }
    }
    public bool Review(DateTimeOffset now)
    {
        if (AttemptState != "Running" || Lease is null || Lease.ExpiresUtc > now) return false;
        AttemptState = "Abandoned"; LocalExitConfirmed = false; Uncertain = true;
        DecideUnknown(now); return true;
    }
    private void DecideUnknown(DateTimeOffset now)
    {
        var retry = RetryDecision.Decide(Policy, Ordinal, null, null, true, StopRequested, null, Random.Shared.NextDouble());
        if (retry.Retry) ScheduleRetry(now + retry.Delay);
        else { State = "NeedsReview"; Events.Add(State); }
    }
    public void Stop(bool force)
    {
        if (Terminal(State)) return;
        StopRequested = true; Force |= force;
        Events.Add(force ? "ForceStopRequested" : "StopRequested");
        if (State is "Queued" or "RetryWaiting") Finish("Stopped");
    }
    public void Resolve(string decision, DateTimeOffset now)
    {
        if (State != "NeedsReview") throw new WorkerDockException("ExecutionNotReviewable");
        if (decision == "ReconcileSucceeded") { Uncertain = false; Finish("Succeeded"); }
        else if (decision == "Cancel") Finish("Stopped");
        else if (decision == "AuthorizeRetry")
        {
            if (Ordinal >= Policy.MaxAttempts) throw new WorkerDockException("AttemptBudgetExhausted");
            StopRequested = false; Force = false; ScheduleRetry(now);
        }
        else throw new WorkerDockException("ValidationFailed", "decision");
    }
    private void ScheduleRetry(DateTimeOffset due)
    {
        var intent = Intent with { DeliveryId = Guid.NewGuid(), AttemptOrdinal = Ordinal + 1, DueUtc = due, PayloadDigest = "" };
        NewDispatch = Intent = intent with { PayloadDigest = intent.ComputeDigest() };
        State = "RetryWaiting"; Events.Add(State);
    }
    private void Finish(string state) { State = state; PermitRetained = false; Events.Add(state); }
}
