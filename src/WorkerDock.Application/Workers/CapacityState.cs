using WorkerDock.Application.Common;
namespace WorkerDock.Application.Workers;

public sealed record CapacityWorker(Guid InstallationId, string BootId, string CatalogDigest, int Ceiling, int LiveLimit,
    long AcknowledgedRevision, string State, DateTimeOffset HeartbeatUtc, bool ReservationsRecovered = false);

// Transaction-local policy model. The SQL adapter serializes every mutation with attempt authorization.
public sealed class CapacityState(string catalogDigest, Dictionary<string, int> limits, long desiredRevision = 0, long effectiveRevision = 0,
    string state = "Pending", IEnumerable<CapacityWorker>? workers = null)
{
    public string CatalogDigest { get; } = catalogDigest;
    public Dictionary<string, int> Limits { get; } = limits;
    public long DesiredRevision { get; private set; } = desiredRevision;
    public long EffectiveRevision { get; private set; } = effectiveRevision;
    public string State { get; private set; } = state;
    public Dictionary<Guid, CapacityWorker> Workers { get; } = (workers ?? []).ToDictionary(x => x.InstallationId);
    public CapacitySnapshot Snapshot => new(DesiredRevision, EffectiveRevision, State, new Dictionary<string, int>(Limits));
    public void SetTemplateLimit(string templateId, int limit)
    {
        if (!Limits.ContainsKey(templateId)) throw new WorkerDockException("UnknownTemplate");
        if (limit < 0) throw new WorkerDockException("ValidationFailed", "limit");
        Limits[templateId] = limit; DesiredRevision++; State = "Pending";
        foreach (var w in Workers.Values.Where(w => w.State == "Ready").ToArray())
            Workers[w.InstallationId] = w with { State = "Standby" };
    }
    public void Register(Guid installationId, string bootId, string digest, int ceiling, DateTimeOffset now)
    {
        if (installationId == Guid.Empty || string.IsNullOrWhiteSpace(bootId) || bootId.Length > 100 || ceiling is < 1 or > 1024)
            throw new WorkerDockException("ValidationFailed", "worker");
        if (Workers.Values.Any(w => w.InstallationId != installationId && w.BootId == bootId)) throw new WorkerDockException("WorkerBootConflict");
        var live = ceiling;
        if (Workers.TryGetValue(installationId, out var prior))
        {
            if (prior.State == "Fenced" && prior.BootId == bootId) throw new WorkerDockException("WorkerFenced");
            if (prior.BootId != bootId && !(prior.State == "Fenced" && prior.ReservationsRecovered)) throw new WorkerDockException("WorkerRecoveryPending");
            if (prior.BootId == bootId && prior.Ceiling != ceiling) throw new WorkerDockException("WorkerCeilingChanged");
            live = Math.Min(prior.LiveLimit, ceiling);
        }
        Workers[installationId] = new(installationId, bootId, digest, ceiling, live, -1,
            digest == CatalogDigest ? "Joining" : "CatalogMismatch", now);
    }
    public void Acknowledge(Guid id, string boot, long revision, DateTimeOffset now)
    {
        if (!Workers.TryGetValue(id, out var w) || w.BootId != boot || w.State is "Fenced" or "CatalogMismatch")
            throw new WorkerDockException("WorkerNotReady");
        if (revision != DesiredRevision) return;
        Workers[id] = w with { AcknowledgedRevision = revision, HeartbeatUtc = now,
            State = State == "Effective" && revision == EffectiveRevision ? "Ready" : "Standby" };
    }
    public bool Promote(DateTimeOffset now)
    {
        var compatible = Workers.Values.Where(w => w.State != "CatalogMismatch").ToArray();
        if (!compatible.Any(w => w.State != "Fenced") || compatible.Any(w =>
            w.State == "Fenced" ? !w.ReservationsRecovered : w.CatalogDigest != CatalogDigest ||
            w.AcknowledgedRevision != DesiredRevision || w.HeartbeatUtc <= now.AddSeconds(-60))) return false;
        EffectiveRevision = DesiredRevision; State = "Effective";
        foreach (var w in compatible.Where(w => w.State != "Fenced")) Workers[w.InstallationId] = w with { State = "Ready" };
        return true;
    }
    public bool CanStart(string boot, string templateId, DateTimeOffset now) => State == "Effective" &&
        Limits.GetValueOrDefault(templateId) > 0 && Workers.Values.Any(w => w.BootId == boot && w.State == "Ready" &&
            w.CatalogDigest == CatalogDigest && w.AcknowledgedRevision == EffectiveRevision &&
            w.LiveLimit > 0 && w.HeartbeatUtc > now.AddSeconds(-60));
    public void SetWorkerLimit(Guid id, int limit)
    {
        if (!Workers.TryGetValue(id, out var w)) throw new WorkerDockException("WorkerNotFound");
        if (limit < 0 || limit > w.Ceiling) throw new WorkerDockException("ValidationFailed", "limit");
        Workers[id] = w with { LiveLimit = limit };
    }
    public void Fence(Guid id, DateTimeOffset now)
    {
        var w = Workers[id];
        if (w.State == "Fenced") return;
        if (w.HeartbeatUtc > now.AddSeconds(-60)) throw new WorkerDockException("WorkerStillAvailable");
        Workers[id] = w with { State = "Fenced", ReservationsRecovered = false };
    }
    public void Recover(Guid id, string boot)
    {
        if (!Workers.TryGetValue(id, out var w) || w.BootId != boot || w.State != "Fenced") throw new WorkerDockException("WorkerNotFenced");
        Workers[id] = w with { ReservationsRecovered = true };
    }
}
