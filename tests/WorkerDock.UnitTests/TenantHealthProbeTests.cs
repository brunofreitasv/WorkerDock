using System.Net;
using System.Text;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Secrets;
using WorkerDock.ControlCenter.Health;
using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.UnitTests;

public sealed class TenantHealthProbeTests
{
    [Fact]
    public async Task Probe_uses_registered_destination_and_secret_and_marks_identity_mismatch()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        var registry = new InMemoryCentralRegistry(time);
        var expectedInstance = Guid.NewGuid();
        var actor = new ActorContext("manager", "Test", Guid.NewGuid());
        var entry = await registry.SaveAsync(null, new("Finance", expectedInstance, "https://finance.example.test/root/", "FINANCE_HEALTH"), null, actor, default);
        var requests = new RecordingHttpClientFactory($"{{\"instanceId\":\"{Guid.NewGuid()}\",\"overall\":\"Healthy\"}}");
        var probe = new TenantHealthProbe(registry, new TestVault(), requests, time);

        var result = await probe.CheckAsync(entry.Id, default);

        Assert.Equal("IdentityMismatch", result.Health);
        Assert.Equal("https://finance.example.test/root/health/ready", requests.RequestUri!.ToString());
        Assert.Equal("secret-value", requests.HealthToken);
        Assert.Equal(time.GetUtcNow(), result.LastAttemptUtc);
        Assert.Null(result.LastSuccessUtc);
    }

    [Fact]
    public async Task Successful_observation_becomes_stale_after_freshness_window()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        var registry = new InMemoryCentralRegistry(time);
        var actor = new ActorContext("manager", "Test", Guid.NewGuid());
        var entry = await registry.SaveAsync(null, new("Finance", Guid.NewGuid(), "https://finance.example.test/", "FINANCE_HEALTH"), null, actor, default);
        var requests = new RecordingHttpClientFactory($"{{\"instanceId\":\"{entry.ExpectedInstanceId}\",\"overall\":\"Healthy\"}}");
        var probe = new TenantHealthProbe(registry, new TestVault(), requests, time);

        Assert.Equal("Healthy", (await probe.CheckAsync(entry.Id, default)).Health);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("Stale", Assert.Single((await registry.ListAsync(null, new(null, 20), default)).Items).Health);
    }

    private sealed class TestVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            Assert.Equal("FINANCE_HEALTH", reference.Name);
            return ValueTask.FromResult(new SecretValue("secret-value"));
        }
    }

    private sealed class RecordingHttpClientFactory(string json) : IHttpClientFactory
    {
        public Uri? RequestUri { get; private set; }
        public string? HealthToken { get; private set; }
        public HttpClient CreateClient(string name) => new(new RecordingHandler(this, json), disposeHandler: true);
        private sealed class RecordingHandler(RecordingHttpClientFactory owner, string json) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.RequestUri = request.RequestUri;
                owner.HealthToken = request.Headers.GetValues("X-WorkerDock-Health").Single();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan interval) => current += interval;
    }
}
