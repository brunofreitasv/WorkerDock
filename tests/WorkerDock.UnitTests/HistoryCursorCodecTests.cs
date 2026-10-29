using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.UnitTests;

public sealed class HistoryCursorCodecTests
{
    [Fact]
    public void RoundTrips_sort_position()
    {
        var instanceId = Guid.NewGuid();
        var time = new DateTimeOffset(2026, 10, 29, 12, 30, 0, TimeSpan.Zero);
        var id = Guid.NewGuid();
        var cursor = HistoryCursorCodec.Encode(instanceId, "executions", "status=failed", time, id, 2);

        var decoded = HistoryCursorCodec.Decode(cursor, instanceId, "executions", "status=failed");

        Assert.Equal(new HistoryPosition(time, id, 2), decoded);
    }

    [Fact]
    public void Rejects_changed_filter_tenant_scope_and_malformed_cursor()
    {
        var instanceId = Guid.NewGuid();
        var cursor = HistoryCursorCodec.Encode(instanceId, "executions", "status=failed",
            DateTimeOffset.UtcNow, Guid.NewGuid(), 2);

        AssertValidationFailed(() => HistoryCursorCodec.Decode(cursor, instanceId, "executions", "status=success"));
        AssertValidationFailed(() => HistoryCursorCodec.Decode(cursor, Guid.NewGuid(), "executions", "status=failed"));
        AssertValidationFailed(() => HistoryCursorCodec.Decode(cursor, instanceId, "alerts", "status=failed"));
        AssertValidationFailed(() => HistoryCursorCodec.Decode("not-a-valid-cursor", instanceId, "executions", "status=failed"));
        AssertValidationFailed(() => HistoryCursorCodec.Decode(new string('a', 2049), instanceId, "executions", "status=failed"));
    }

    private static void AssertValidationFailed(Action action)
    {
        var error = Assert.Throws<WorkerDockException>(action);
        Assert.Equal("ValidationFailed", error.Code);
    }
}
