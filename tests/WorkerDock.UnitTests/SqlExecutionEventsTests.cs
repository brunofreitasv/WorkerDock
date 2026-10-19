using WorkerDock.Application.Common;
using WorkerDock.Application.Observability;
using WorkerDock.Infrastructure.Observability;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.UnitTests;

public sealed class SqlExecutionEventsTests
{
    [Fact]
    public async Task Template_failed_event_accepts_connectivity_failed_safe_code_before_persistence()
    {
        var events = new SqlExecutionEvents(new TenantDatabaseOptions(
            "Server=127.0.0.1,1;Database=WorkerDock;User Id=worker;Password=invalid;Encrypt=False;Connect Timeout=1;",
            Guid.NewGuid()));
        var item = new ExecutionEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Handler",
            1,
            DateTimeOffset.UtcNow,
            "TemplateFailed",
            new Dictionary<string, string> { ["safeCode"] = "ConnectivityFailed" });

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await events.AppendAsync([item], CancellationToken.None));

        Assert.Equal("PersistenceUnavailable", error.Code);
    }
}
