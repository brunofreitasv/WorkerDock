using WorkerDock.Application.Common;
using WorkerDock.Application.Retention;
using WorkerDock.Infrastructure.Retention;

namespace WorkerDock.UnitTests;

public sealed class InMemoryRetentionServiceTests
{
    [Fact]
    public async Task Save_uses_row_version_and_returns_audit_receipt()
    {
        var service = new InMemoryRetentionService();
        var current = await service.GetPolicyAsync(CancellationToken.None);
        var actor = new ActorContext("operator", "Test", Guid.NewGuid());

        var receipt = await service.SavePolicyAsync(new(14, 45, 180, false, 0), current.Version, actor, CancellationToken.None);

        var saved = await service.GetPolicyAsync(CancellationToken.None);
        Assert.Equal(new RetentionPolicy(14, 45, 180, false, 0), saved.Policy);
        Assert.NotEqual(current.Version, saved.Version);
        Assert.Equal(saved.Policy, service.Policy);
        Assert.Equal(RetentionPolicy.TargetId, receipt.TargetId);
    }

    [Fact]
    public async Task Save_rejects_stale_versions_without_changing_policy()
    {
        var service = new InMemoryRetentionService();
        var current = await service.GetPolicyAsync(CancellationToken.None);
        await service.SavePolicyAsync(new(14, 45, 180, false, 0), current.Version,
            new ActorContext("operator", "Test", Guid.NewGuid()), CancellationToken.None);

        await Assert.ThrowsAsync<WorkerDockException>(async () => await service.SavePolicyAsync(new(15, 45, 180, false, 0), current.Version,
            new ActorContext("operator", "Test", Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(new RetentionPolicy(14, 45, 180, false, 0), service.Policy);
    }
}
