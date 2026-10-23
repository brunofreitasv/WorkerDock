using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Application.Common;
using WorkerDock.Application.Settings;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Settings;

namespace WorkerDock.IntegrationTests;

public sealed class IntegrationSettingsTests
{
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Sql_revision_history_rowversion_audit_and_rollback_guard_are_durable()
    {
        await using var database = new SqlDatabaseFixture(); await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot);
        await runner.ApplyAsync("tenant", "0001_initial", default);
        var settings = new SqlIntegrationSettings(new(database.ConnectionString, database.InstanceId));
        var actor = new ActorContext("manager", "test", Guid.NewGuid());
        var vault = await settings.GetVaultAsync(default);
        await settings.SaveVaultAsync(vault, actor, default);
        await Assert.ThrowsAsync<WorkerDockException>(async () => await settings.SaveVaultAsync(vault, actor, default));
        var egress = await settings.GetEgressAsync("tenant-jobs", default);
        await settings.SaveEgressAsync(egress with { Hosts = ["api.example.test"] }, actor, default);
        var current = await settings.GetEgressAsync("tenant-jobs", default);
        Assert.Equal("api.example.test", Assert.Single(current.Hosts));
        Assert.Equal(2, await database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].IntegrationSettingsRevisions WHERE SettingsKind=N'Egress' AND SettingsName=N'tenant-jobs'"));

        await database.ExecuteAsync("CREATE TRIGGER [workerdock].IntegrationAuditFailure ON [workerdock].AuditEvents AFTER INSERT AS THROW 51027,'Synthetic audit failure',1;");
        var failed = await Assert.ThrowsAsync<WorkerDockException>(async () => await settings.SaveEgressAsync(current with { Hosts = ["other.example.test"] }, actor, default));
        Assert.Equal("PersistenceUnavailable", failed.Code);
        await database.ExecuteAsync("DROP TRIGGER [workerdock].IntegrationAuditFailure");
        Assert.Equal("api.example.test", Assert.Single((await settings.GetEgressAsync("tenant-jobs", default)).Hosts));
        await Assert.ThrowsAsync<WorkerDockException>(async () => await runner.RollbackAsync("tenant", "0001_initial", default));
    }

    [Fact]
    public async Task Manager_settings_are_versioned_validated_and_snapshotted_without_secret_values()
    {
        var settings = new InMemoryIntegrationSettings();
        var before = await settings.GetSnapshotAsync(default);
        var actor = new ActorContext("manager", "Anonymous", Guid.NewGuid());
        var job = new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(), 1, "HTTP", "workerdock.http", "1.0.0",
            JsonSerializer.SerializeToElement(new { }), null, AttemptLifecycleTests.Policy());
        var jobs = new InMemoryJobCommands(TimeProvider.System, [job], integrationSettings: settings, templateCatalog: JobCommandTestCatalog.Create());
        var firstReceipt = await jobs.RunNowAsync(job.JobId, "before-settings-update", actor, default);
        var firstStore = jobs.GetAttemptStore(firstReceipt.TargetId)!;
        var firstLease = await firstStore.BeginAsync(firstStore.Intent, "boot", default);
        var firstDescriptor = await firstStore.GetDescriptorAsync(firstLease.Lease!, default);
        var current = await settings.GetEgressAsync("tenant-jobs", default);
        await settings.SaveEgressAsync(current with { Hosts = ["api.example.test"], Ports = [443], PrivateCidrs = [] }, actor, default);
        await Assert.ThrowsAsync<WorkerDockException>(async () => await settings.SaveEgressAsync(current with { Hosts = ["stale.example.test"] }, actor, default));
        var denied = await Assert.ThrowsAsync<WorkerDockException>(async () => await settings.SaveEgressAsync(current with { Hosts = ["metadata.test"], PrivateCidrs = ["127.0.0.1/8"] }, actor, default));
        Assert.Equal("DeploymentPolicyViolation", denied.Code);
        var after = await settings.GetSnapshotAsync(default);
        Assert.True(after.VaultRevision >= before.VaultRevision);
        Assert.Equal("api.example.test", Assert.Single(after.Policies.Single(x => x.Name == "tenant-jobs").Hosts));
        var secondReceipt = await jobs.RunNowAsync(job.JobId, "after-settings-update", actor, default);
        var secondStore = jobs.GetAttemptStore(secondReceipt.TargetId)!;
        var secondLease = await secondStore.BeginAsync(secondStore.Intent, "boot", default);
        var secondDescriptor = await secondStore.GetDescriptorAsync(secondLease.Lease!, default);
        var previous = IntegrationConfigurationJson.Deserialize(firstDescriptor.IntegrationConfiguration);
        var updated = IntegrationConfigurationJson.Deserialize(secondDescriptor.IntegrationConfiguration);
        Assert.Empty(previous.Policies.Single(x => x.Name == "tenant-jobs").Hosts);
        Assert.Equal("api.example.test", Assert.Single(updated.Policies.Single(x => x.Name == "tenant-jobs").Hosts));
        Assert.NotEmpty(settings.AuditEvents);
        Assert.DoesNotContain("secret-value-sentinel", JsonSerializer.Serialize(new { firstDescriptor, secondDescriptor, audits = settings.AuditEvents }));
    }

    [Fact]
    public async Task Manager_only_endpoints_reject_identity_overrides_and_use_if_match()
    {
        var settings = new InMemoryIntegrationSettings();
        await using var baseHost = new AccessHost();
        await using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IIntegrationSettings>(settings)));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        await client.GetAsync("/api/session");
        var csrf = await client.GetFromJsonAsync<CsrfView>("/api/antiforgery");
        using var get = await client.GetAsync("/api/settings/vault");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var version = get.Headers.ETag!.Tag;
        using var update = Request(HttpMethod.Put, "/api/settings/vault", new { providerId = "uninstalled.provider", publicOptions = new { } }, csrf!.RequestToken, version);
        using var conflict = await client.SendAsync(update);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var identity = Request(HttpMethod.Put, "/api/settings/vault", new { providerId = "workerdock.environment", publicOptions = new { }, tenantId = Guid.NewGuid(), bootstrapIdentity = "deployment" }, csrf.RequestToken, version);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(identity)).StatusCode);

        var current = await settings.GetEgressAsync("tenant-jobs", default);
        using var egress = Request(HttpMethod.Put, "/api/settings/egress/tenant-jobs", new { hosts = new[] { "api.example.test" }, ports = new[] { 443 }, privateCidrs = Array.Empty<string>(), allowHttp = false }, csrf.RequestToken, '"' + Convert.ToBase64String(current.Version) + '"');
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(egress)).StatusCode);
        using var stale = Request(HttpMethod.Put, "/api/settings/egress/tenant-jobs", new { hosts = new[] { "stale.example.test" }, ports = new[] { 443 }, privateCidrs = Array.Empty<string>(), allowHttp = false }, csrf.RequestToken, '"' + Convert.ToBase64String(current.Version) + '"');
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);

        using var issuer = new LocalOidcIssuer("Viewer");
        await using var protectedHost = new AccessHost(issuer, "Manager");
        using var viewer = protectedHost.Client(); await issuer.SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/settings/vault")).StatusCode);
        var viewerCsrf = await viewer.GetFromJsonAsync<CsrfView>("/api/antiforgery");
        using var viewerUpdate = Request(HttpMethod.Put, "/api/settings/egress/tenant-jobs", new { hosts = Array.Empty<string>(), ports = new[] { 443 }, privateCidrs = Array.Empty<string>(), allowHttp = false }, viewerCsrf!.RequestToken, '"' + Convert.ToBase64String(current.Version) + '"');
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(viewerUpdate)).StatusCode);
    }

    [Fact]
    public async Task Child_policy_snapshot_cannot_disable_hard_denials()
    {
        var settings = new InMemoryIntegrationSettings();
        var snapshot = await settings.GetSnapshotAsync(default);
        var descriptor = AttemptLifecycleTests.Descriptor() with { IntegrationConfiguration = IntegrationConfigurationJson.Serialize(snapshot) };
        var policy = WorkerDock.Infrastructure.Outbound.OutboundPolicy.FromExecutionDescriptor(descriptor);
        await Assert.ThrowsAsync<WorkerDock.Application.Outbound.OutboundPolicyException>(async () =>
            await policy.ResolveAsync(new(new Uri("http://127.0.0.1/"), "tenant-jobs"), default));
    }

    [Fact]
    public async Task Child_policy_uses_the_saved_host_port_and_http_options()
    {
        var settings = new InMemoryIntegrationSettings();
        var actor = new ActorContext("manager", "Anonymous", Guid.NewGuid());
        var current = await settings.GetEgressAsync("tenant-jobs", default);
        await settings.SaveEgressAsync(current with { Hosts = ["8.8.8.8"], Ports = [8080], AllowHttp = true }, actor, default);
        var snapshot = await settings.GetSnapshotAsync(default);
        var descriptor = AttemptLifecycleTests.Descriptor() with { IntegrationConfiguration = IntegrationConfigurationJson.Serialize(snapshot) };
        var policy = WorkerDock.Infrastructure.Outbound.OutboundPolicy.FromExecutionDescriptor(descriptor);

        await policy.ResolveAsync(new(new Uri("http://8.8.8.8:8080/"), "tenant-jobs"), default);
    }

    [Fact]
    public async Task Retry_attempt_uses_latest_settings_revision_while_first_attempt_keeps_its_snapshot()
    {
        var settings = new InMemoryIntegrationSettings();
        var clock = new AttemptLifecycleTests.Clock();
        var actor = new ActorContext("manager", "Anonymous", Guid.NewGuid());
        var job = new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(), 1, "HTTP", "workerdock.http", "1.0.0",
            JsonSerializer.SerializeToElement(new { }), null, AttemptLifecycleTests.Policy(attempts: 2));
        var jobs = new InMemoryJobCommands(clock, [job], integrationSettings: settings, templateCatalog: JobCommandTestCatalog.Create());
        var receipt = await jobs.RunNowAsync(job.JobId, "same-execution-retry", actor, default);
        var store = jobs.GetAttemptStore(receipt.TargetId)!;
        var firstLease = (await store.BeginAsync(store.Intent, "boot", default)).Lease!;
        var firstDescriptor = await store.GetDescriptorAsync(firstLease, default);
        await store.CompleteAsync(firstLease, new(new WorkerDock.Contracts.Templates.TemplateResult("TransientFailure", "Busy", false,
            JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);

        var current = await settings.GetEgressAsync("tenant-jobs", default);
        await settings.SaveEgressAsync(current with { Hosts = ["api.example.test"] }, actor, default);
        var retry = Assert.Single(store.Retries);
        clock.Now = retry.DueUtc;
        var secondLease = (await store.BeginAsync(retry, "boot", default)).Lease!;
        var secondDescriptor = await store.GetDescriptorAsync(secondLease, default);

        var first = IntegrationConfigurationJson.Deserialize(firstDescriptor.IntegrationConfiguration);
        var second = IntegrationConfigurationJson.Deserialize(secondDescriptor.IntegrationConfiguration);
        Assert.Empty(first.Policies.Single(x => x.Name == "tenant-jobs").Hosts);
        Assert.Equal("api.example.test", Assert.Single(second.Policies.Single(x => x.Name == "tenant-jobs").Hosts));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object body, string csrf, string? etag = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "https://localhost"); request.Headers.Add("X-WorkerDock-CSRF", csrf);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }
}
