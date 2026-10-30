using System.Net;
using System.Net.Http.Json;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Scheduling;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace WorkerDock.IntegrationTests;

public sealed class JobManagementAccessTests
{
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Manager_reads_coherent_sql_detail_etag_and_definition_write_requires_csrf_and_if_match()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "API draft");
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        using var issuer = new LocalOidcIssuer("Manager");
        await using var baseHost = new AccessHost(issuer, "Manager", extra: new() { ["Logging:LogLevel:Default"] = "Warning" });
        await using var host = baseHost.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IJobManagementQueries>(new SqlJobManagementQueries(options));
            services.AddSingleton<IJobDefinitionCommands>(new SqlJobDefinitionCommands(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System));
        }));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await issuer.SignIn(client);
        using var detailResponse = await client.GetAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode); Assert.NotNull(detailResponse.Headers.ETag);
        var detail = (await detailResponse.Content.ReadFromJsonAsync<JobManagementDetailDto>())!;
        Assert.Equal('"' + Convert.ToBase64String(detail.Editor.Version) + '"', detailResponse.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/jobs?view=disabled")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/jobs?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{Guid.NewGuid()}/executions")).StatusCode);
        var input = new { revision = detail.Editor.Revision with { Name = "Changed through API" }, schedule = (object?)null, missedPolicy = (object?)null };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/jobs/{id}/definition", input)).StatusCode);
        var csrf = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        foreach (var omitted in new object[] { new { revision = detail.Editor.Revision }, new { revision = detail.Editor.Revision, schedule = (object?)null }, new { revision = detail.Editor.Revision, missedPolicy = (object?)null } })
        {
            using var omittedSelection = new HttpRequestMessage(HttpMethod.Put, $"/api/jobs/{id}/definition") { Content = JsonContent.Create(omitted) };
            omittedSelection.Headers.Add("Origin", "https://localhost"); omittedSelection.Headers.Add("X-WorkerDock-CSRF", csrf); omittedSelection.Headers.IfMatch.Add(detailResponse.Headers.ETag);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(omittedSelection)).StatusCode);
        }
        HttpRequestMessage Write(bool version)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/jobs/{id}/definition") { Content = JsonContent.Create(input) };
            request.Headers.Add("Origin", "https://localhost"); request.Headers.Add("X-WorkerDock-CSRF", csrf);
            if (version) request.Headers.IfMatch.Add(detailResponse.Headers.ETag);
            return request;
        }
        using var noVersion = Write(false); Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(noVersion)).StatusCode);
        using var valid = Write(true); Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(valid)).StatusCode);
        using var stale = Write(true); Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
    }
    [Fact]
    public async Task Viewer_can_read_safe_job_routes_but_not_configuration_or_write_controls()
    {
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var host = new AccessHost(issuer, "Manager");
        using var client = host.Client(); await issuer.SignIn(client);
        var id = Guid.NewGuid();
        foreach (var route in new[] { "/api/jobs", $"/api/jobs/{id}/execution-controls" })
            Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
        foreach (var route in new[] { $"/api/jobs/{id}", $"/api/jobs/{id}/executions" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
        var csrf = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/jobs/{id}/execution-controls") { Content = JsonContent.Create(new { limit = 2, pauseOnNeedsReview = true }) };
        update.Headers.Add("Origin", "https://localhost"); update.Headers.Add("X-WorkerDock-CSRF", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(update)).StatusCode);
    }

    [Fact]
    public async Task Viewer_gets_only_execution_control_values()
    {
        var id = Guid.NewGuid();
        using var issuer = new LocalOidcIssuer("Viewer");
        await using var baseHost = new AccessHost(issuer, "Manager");
        await using var host = baseHost.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddSingleton<IJobManagementQueries>(new FixedJobManagementQueries())));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await issuer.SignIn(client);

        using var list = await client.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var response = await client.GetAsync($"/api/jobs/{id}/execution-controls");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "limit", "pauseOnNeedsReview" }, json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(3, json.RootElement.GetProperty("limit").GetInt32());
        Assert.True(json.RootElement.GetProperty("pauseOnNeedsReview").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/jobs/{id}")).StatusCode);
    }

    private sealed class FixedJobManagementQueries : IJobManagementQueries
    {
        public ValueTask<Page<JobListItemDto>> ListAsync(JobListFilter filter, PageRequest page, CancellationToken ct) =>
            ValueTask.FromResult(new Page<JobListItemDto>([], null));
        public ValueTask<JobManagementDetailDto> GetAsync(Guid id, CancellationToken ct) =>
            ValueTask.FromResult(new JobManagementDetailDto(new JobEditorDto(null!, [], false, false, 3, true), null, null, false, null));
        public ValueTask<Page<JobExecutionItemDto>> ExecutionsAsync(Guid id, PageRequest page, CancellationToken ct) =>
            ValueTask.FromResult(new Page<JobExecutionItemDto>([], null));
    }

    [Fact]
    public async Task Manager_receives_safe_unavailable_in_memory_mode()
    {
        using var issuer = new LocalOidcIssuer("Manager");
        await using var host = new AccessHost(issuer, "Manager");
        using var client = host.Client(); await issuer.SignIn(client);
        var id = Guid.NewGuid();
        foreach (var route in new[] { "/api/jobs", $"/api/jobs/{id}", $"/api/jobs/{id}/executions" })
        {
            var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("PersistenceUnavailable", await response.Content.ReadAsStringAsync());
        }
    }
}
