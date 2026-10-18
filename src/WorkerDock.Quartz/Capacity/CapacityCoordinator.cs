using global::Quartz;
using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;

namespace WorkerDock.Quartz.Capacity;

// Business transactions never contain a scheduler call. A stale acknowledgement cannot promote a newer revision.
public sealed class CapacityCoordinator(ICapacityStore store, IScheduler scheduler, Guid installationId, string bootId,
    ExecutionLimitScope scope = ExecutionLimitScope.Cluster)
{
    private long applied = -1;
    public async ValueTask<CapacitySnapshot> ApplyPendingAsync(CancellationToken ct)
    {
        var current = await store.TransactAsync((s, now) =>
        {
            foreach (var w in s.Workers.Values.Where(w => w.State != "CatalogMismatch" && w.State != "Fenced" && w.HeartbeatUtc <= now.AddSeconds(-60)).ToArray()) s.Fence(w.InstallationId, now);
            return (s.Snapshot, Workers: s.Workers.Values.ToArray());
        }, ct);
        var clusterNodes = await scheduler.QueryClusterNodes(ct);
        foreach (var w in current.Workers.Where(w => w.State == "Fenced" && !w.ReservationsRecovered))
            if (!clusterNodes.Any(node => node.InstanceId == w.BootId) &&
                (await scheduler.QueryFireInstances(new FireInstanceQuery { SchedulerInstanceId = w.BootId, State = null, Take = 1 }, ct)).Items.Count == 0)
                await store.TransactAsync((s, _) => { s.Recover(w.InstallationId, w.BootId); return true; }, ct);
        var local = current.Workers.SingleOrDefault(w => w.InstallationId == installationId && w.BootId == bootId);
        if (local is null || local.State is "CatalogMismatch" or "Fenced")
        {
            await scheduler.Standby(ct); throw new WorkerDockException(local?.State == "CatalogMismatch" ? "CatalogMismatch" : "WorkerNotReady");
        }
        if (applied != current.Snapshot.DesiredRevision || current.Snapshot.State != "Effective" || local.State != "Ready")
        {
            await scheduler.Standby(ct);
            var limits = ExecutionLimitsBuilder.Create().ForDefaultGroup(0, scope).ForOtherGroups(0, scope);
            foreach (var pair in current.Snapshot.TemplateLimits) limits.ForGroup(pair.Key, pair.Value, scope);
            await scheduler.SetExecutionLimits(limits.Build(), ct);
            applied = current.Snapshot.DesiredRevision;
        }
        var result = await store.TransactAsync((s, now) =>
        {
            s.Acknowledge(installationId, bootId, applied, now); s.Promote(now);
            return (s.Snapshot, Ready: s.Workers[installationId].State == "Ready" && s.Workers[installationId].AcknowledgedRevision == s.EffectiveRevision);
        }, ct);
        if (result.Snapshot.State == "Effective" && result.Ready && result.Snapshot.EffectiveRevision == applied) await scheduler.Start(ct);
        return result.Snapshot;
    }
}
