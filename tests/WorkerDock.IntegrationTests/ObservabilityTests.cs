using System.Net;
using System.Data;
using System.Reflection;
using WorkerDock.Infrastructure.Observability;

namespace WorkerDock.IntegrationTests;

public sealed class ObservabilityTests
{
    [Fact]
    public void Metric_aggregation_uses_read_committed_update_lock_claims()
    {
        var isolation = typeof(MetricAggregation).GetField("BatchIsolationLevel", BindingFlags.Static | BindingFlags.NonPublic);
        var hints = typeof(MetricAggregation).GetField("BatchClaimHints", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(isolation);
        Assert.Equal(IsolationLevel.ReadCommitted, isolation!.GetValue(null));
        Assert.NotNull(hints);
        var claimHints = Assert.IsType<string>(hints!.GetRawConstantValue());
        Assert.Equal("UPDLOCK,READPAST,READCOMMITTEDLOCK", claimHints);
        Assert.DoesNotContain("ROWLOCK", claimHints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_event_route_rejects_unbounded_pages_before_querying_storage()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var host = new AccessHost(issuer, "Manager");
        using var client = host.Client();
        await issuer.SignIn(client);

        using var response = await client.GetAsync($"/api/monitoring/executions/{Guid.NewGuid()}/events?limit=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Observability_migration_only_adds_audit_select_to_runtime_role()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "database", "tenant", "0006_observability.up.sql"));
        Assert.Contains("GRANT SELECT ON [workerdock].AuditEvents TO WorkerDockRuntime", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT UPDATE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT DELETE", migration, StringComparison.OrdinalIgnoreCase);
    }
}
