using System.Collections.Immutable;
using System.Text;
using WorkerDock.Application.Common;
using WorkerDock.Domain.Jobs;

namespace WorkerDock.Application.Jobs;

public static class JobCommandValidation
{
    public static void ValidateActor(ActorContext actor)
    {
        if (actor is null || string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 300 ||
            actor.AuthenticationMode is not ("Entra" or "Anonymous") || actor.CorrelationId == Guid.Empty)
            throw new WorkerDockException("ValidationFailed", "actor");
    }

    public static void ValidateRevision(JobRevision revision)
    {
        if (revision.JobId == Guid.Empty) throw new WorkerDockException("ValidationFailed", "jobId");
        if (string.IsNullOrWhiteSpace(revision.Name) || revision.Name.Length > 200)
            throw new WorkerDockException("ValidationFailed", "name");
        if (!((revision.TemplateId, revision.TemplateVersion) is ("workerdock.sql-server", "1.0.0") or ("workerdock.http", "1.0.0")))
            throw new WorkerDockException("UnknownTemplate", "templateId");
        if (revision.Configuration.ValueKind != System.Text.Json.JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(revision.Configuration.GetRawText()) > 65536)
            throw new WorkerDockException("ValidationFailed", "configuration");
        var policy = revision.Policy;
        if (policy is null || (policy.Timeout is { } timeout && timeout <= TimeSpan.Zero) ||
            policy.MaxAttempts is < 1 or > 100 || policy.RetryBaseDelay < TimeSpan.Zero ||
            policy.RetryMaxDelay < policy.RetryBaseDelay || policy.RetryMaxDelay > TimeSpan.FromDays(7) ||
            !double.IsFinite(policy.JitterRatio) || policy.JitterRatio is < 0 or > 1 ||
            policy.Overlap is not ("Queue" or "Skip" or "Allow") || policy.BacklogLimit is < 0 or > 10000 ||
            policy.Priority is < 0 or > 100 || policy.RetryableCodes is null || policy.RetryableCodes.Count > 32 ||
            policy.RetryableCodes.Any(code => string.IsNullOrWhiteSpace(code) || code.Length > 100))
            throw new WorkerDockException("ValidationFailed", "policy");
    }

    public static void ValidateRequestKey(Guid jobId, string key, ActorContext actor)
    {
        ValidateActor(actor);
        if (jobId == Guid.Empty) throw new WorkerDockException("ValidationFailed", "jobId");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl))
            throw new WorkerDockException("ValidationFailed", "requestKey");
    }

    public static JobRevision Freeze(JobRevision revision) => revision with
    {
        Configuration = revision.Configuration.Clone(),
        Policy = revision.Policy with { RetryableCodes = revision.Policy.RetryableCodes.ToImmutableArray() }
    };
}
