using WorkerDock.Application.Common;
using WorkerDock.Application.Workers;
using WorkerDock.Infrastructure.Executions;
using System.Collections.Concurrent;

namespace WorkerDock.IntegrationTests;

public sealed class CapacityPolicyTests
{
    private sealed class MemoryCapacityStore(CapacityState state, DateTimeOffset now) : ICapacityStore
    {
        private readonly SemaphoreSlim gate = new(1, 1);

        public async ValueTask<T> TransactAsync<T>(Func<CapacityState, DateTimeOffset, T> change, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try { return change(state, now); }
            finally { gate.Release(); }
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static CapacityState Policy() => new("catalog", new() { ["sql"] = 3 });
    private static Guid Register(CapacityState p, string boot = "a", string digest = "catalog", int ceiling = 3)
    {
        var id = Guid.NewGuid(); p.Register(id, boot, digest, ceiling, Now); return id;
    }
    private static void Ready(CapacityState p, Guid id, string boot = "a")
    { p.Acknowledge(id, boot, p.DesiredRevision, Now); Assert.True(p.Promote(Now)); }

    [Fact]
    public async Task Concurrent_stale_deliveries_never_authorize_through_committed_closed_gate()
    {
        var p=Policy(); var id=Register(p); Ready(p,id); var memory=new MemoryCapacityStore(p,Now);
        var attempts=Enumerable.Range(0,40).Select(_ => new InMemoryAttemptStore(new AttemptLifecycleTests.Clock { Now=Now },
            AttemptLifecycleTests.Descriptor() with { TemplateId="sql" },AttemptLifecycleTests.Policy(),memory)).ToArray();
        var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts=attempts.Select(async store => { await closed.Task; return await store.BeginAsync(store.Intent,"a",default); }).ToArray();
        await memory.TransactAsync((state,_) => { state.SetTemplateLimit("sql",1); return true; },default);
        closed.SetResult(); var results=await Task.WhenAll(starts);
        Assert.All(results,result => Assert.Equal("Held",result.Decision));
        Assert.All(attempts,store => Assert.Equal(0,store.Snapshot.Ordinal));
    }
    [Fact]
    public async Task Delivery_acquired_before_policy_change_cannot_authorize_after_reopen()
    {
        var p = Policy(); var id = Register(p); Ready(p, id);
        var memory = new MemoryCapacityStore(p, Now);
        var attempts = new InMemoryAttemptStore(new AttemptLifecycleTests.Clock { Now = Now },
            AttemptLifecycleTests.Descriptor() with { TemplateId = "sql" }, AttemptLifecycleTests.Policy(), memory);
        var acquired = attempts.Intent;
        await memory.TransactAsync((state, _) => { state.SetTemplateLimit("sql", 1); return true; }, default);
        Ready(p, id);
        Assert.Equal("Held", (await attempts.BeginAsync(acquired, "a", default)).Decision);
        Assert.Equal(0, attempts.Snapshot.Ordinal);
    }
    [Fact]
    public void Offline_node_cannot_rejoin_with_old_policy()
    {
        var p = Policy(); var a = Register(p); var b = Register(p, "b");
        p.Acknowledge(a, "a", 0, Now); p.Acknowledge(b, "b", 0, Now); Assert.True(p.Promote(Now));
        Assert.True(p.CanStart("b", "sql", Now));
        p.SetTemplateLimit("sql", 1);
        p.Acknowledge(a, "a", 1, Now);
        Assert.False(p.Promote(Now)); Assert.Equal("Pending", p.Snapshot.State);
        Assert.False(p.CanStart("a", "sql", Now)); Assert.False(p.CanStart("b", "sql", Now));
        p.Acknowledge(b, "b", 0, Now); // A stale acknowledgement cannot open the gate.
        Assert.False(p.Promote(Now));
        p.Acknowledge(b, "b", 1, Now); Assert.True(p.Promote(Now));
        Assert.True(p.CanStart("b", "sql", Now));
    }

    [Fact]
    public void Unavailable_node_requires_fence_and_runtime_recovery_before_promotion()
    {
        var p = Policy(); var a = Register(p); var b = Register(p, "b");
        p.Acknowledge(a, "a", 0, Now); p.Acknowledge(b, "b", 0, Now); p.Promote(Now);
        p.SetTemplateLimit("sql", 1); p.Acknowledge(a, "a", 1, Now.AddMinutes(2));
        p.Fence(b, Now.AddMinutes(2)); Assert.False(p.Promote(Now.AddMinutes(2)));
        p.Recover(b, "b"); Assert.True(p.Promote(Now.AddMinutes(2)));
        Assert.False(p.CanStart("b", "sql", Now.AddMinutes(2)));
        Assert.Throws<WorkerDockException>(() => p.Register(b, "b", "catalog", 3, Now.AddMinutes(2)));
    }

    [Fact]
    public void Catalog_and_host_ceiling_are_checked_before_admission()
    {
        var p = Policy(); var bad = Register(p, digest: "old");
        Assert.Equal("CatalogMismatch", p.Workers[bad].State); Assert.False(p.CanStart("a", "sql", Now));
        Assert.Throws<WorkerDockException>(() => Register(p, "other", ceiling: 0));
        var good = Register(p, "good"); Ready(p, good, "good");
        Assert.Throws<WorkerDockException>(() => p.SetWorkerLimit(good, 4));
        p.SetWorkerLimit(good, 0); Assert.False(p.CanStart("good", "sql", Now));
    }
}
