using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Application.Connections;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Infrastructure.Jobs;
namespace WorkerDock.IntegrationTests;
public sealed class SqlProfileEndpointTests
{
    [Fact]
    public async Task Manager_only_reads_mutations_require_csrf_and_mock_diagnostics_never_execute()
    {
        using var issuer=new LocalOidcIssuer("Viewer");await using var protectedHost=new AccessHost(issuer,"Manager");using var viewer=protectedHost.Client();await issuer.SignIn(viewer);
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync("/api/connection-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync($"/api/connection-profiles/{Guid.NewGuid()}")).StatusCode);
        var csrfViewer=await viewer.GetFromJsonAsync<CsrfView>("/api/antiforgery");
        using var viewerPost=Request(HttpMethod.Post,$"/api/connection-profiles/{Guid.NewGuid()}/diagnostics",new { catalog="Target" },csrfViewer!.RequestToken);
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.SendAsync(viewerPost)).StatusCode);
        var profiles=new InMemoryConnectionProfiles();await using var baseHost=new AccessHost();
        await using var host=baseHost.WithWebHostBuilder(b=>b.ConfigureServices(s=>s.AddSingleton<IConnectionProfiles>(profiles)));using var client=host.CreateClient(new(){BaseAddress=new("https://localhost")});
        await client.GetAsync("/api/session");
        var profile=SqlTemplateTests.Profile() with { ProfileId=Guid.Empty,ProfileRevisionId=Guid.Empty,Revision=0 };
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/connection-profiles",profile)).StatusCode);
        var csrf=await client.GetFromJsonAsync<CsrfView>("/api/antiforgery");
        using var post=Request(HttpMethod.Post,"/api/connection-profiles",profile,csrf!.RequestToken);Assert.Equal(HttpStatusCode.Accepted,(await client.SendAsync(post)).StatusCode);
        var current=Assert.Single(await profiles.ListAsync(default));
        using var get=await client.GetAsync($"/api/connection-profiles/{current.Current.ProfileId}");Assert.Equal(HttpStatusCode.OK,get.StatusCode);Assert.NotNull(get.Headers.ETag);
        using var stale=Request(HttpMethod.Put,$"/api/connection-profiles/{current.Current.ProfileId}",current.Current,csrf.RequestToken);Assert.Equal(HttpStatusCode.PreconditionFailed,(await client.SendAsync(stale)).StatusCode);
        using var diagnostic=Request(HttpMethod.Post,$"/api/connection-profiles/{current.Current.ProfileId}/diagnostics",new { catalog="Target" },csrf.RequestToken);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.SendAsync(diagnostic)).StatusCode);
        using var injected=Request(HttpMethod.Post,$"/api/connection-profiles/{current.Current.ProfileId}/diagnostics",new { catalog="Target",commandText="DROP TABLE x" },csrf.RequestToken);Assert.Equal(HttpStatusCode.BadRequest,(await client.SendAsync(injected)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/sql/execute")).StatusCode);
    }
    private static HttpRequestMessage Request(HttpMethod method,string uri,object body,string csrf)
    {
        var request=new HttpRequestMessage(method,uri){Content=JsonContent.Create(body)};request.Headers.Add("Origin","https://localhost");request.Headers.Add("X-WorkerDock-CSRF",csrf);return request;
    }
    [Fact]
    public async Task Execution_pins_revision_after_profile_rotation()
    {
        var profiles=new InMemoryConnectionProfiles();var original=SqlTemplateTests.Profile();var actor=new WorkerDock.Application.Common.ActorContext("manager","Anonymous",Guid.NewGuid());
        await profiles.SaveAsync(original,null,actor,default);var first=await profiles.GetCurrentAsync(original.ProfileId,default);
        var job=new WorkerDock.Domain.Jobs.JobRevision(Guid.NewGuid(),1,"SQL job","workerdock.sql-server","1.0.0",SqlTemplateTests.Json(SqlTemplateTests.Config(original.ProfileId)),original.ProfileId,AttemptLifecycleTests.Policy());
        var jobs=new InMemoryJobCommands(TimeProvider.System,[job],profiles:profiles,templateCatalog:JobCommandTestCatalog.Create());var receipt=await jobs.RunNowAsync(job.JobId,"pin",actor,default);
        await profiles.SaveAsync(original with { Server="rotated" },first.Version,actor,default);
        var store=jobs.GetAttemptStore(receipt.TargetId)!;var started=await store.BeginAsync(store.Intent,"boot",default);Assert.NotNull(started.Lease);
        var execution=await store.GetDescriptorAsync(started.Lease!,default);
        Assert.Equal(first.Current.ProfileRevisionId,execution.ProfileRevisionId);
        Assert.Equal(original.Server,execution.ProfileConfiguration!.Value.GetProperty("Server").GetString());
    }
}
