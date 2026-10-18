using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Logging;
using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Quartz.Dispatch;
using WorkerDock.Quartz.Hosting;
namespace WorkerDock.IntegrationTests;

public sealed class SecretProjectionTests
{
    [Fact]
    public async Task Rotation_preserves_nonsecret_application_Quartz_and_API_records()
    {
        var oldSentinel="OLD_SYNTHETIC_"+Guid.NewGuid().ToString("N"); var newSentinel="NEW_SYNTHETIC_"+Guid.NewGuid().ToString("N");
        var current=oldSentinel;
        var vault=new EnvironmentSecretVault(new Dictionary<string,string> { ["http.credential"]="SYNTHETIC_PROVIDER_SLOT" },_=>current);
        var job=new JobRevision(Guid.NewGuid(),0,"Rotation","workerdock.http","1.0.0",JsonSerializer.SerializeToElement(new { credentialReference="http.credential" }),null,AttemptLifecycleTests.Policy());
        var commands=new InMemoryJobCommands(TimeProvider.System,[job]);
        await using var baseHost=new AccessHost();
        await using var host=baseHost.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<IJobCommands>(commands)));
        using var client=host.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false }); await client.GetAsync("/api/session");
        var id=Guid.NewGuid(); await using var topology=QuartzTopology.CreateMemory(id,"Development");
        var publisher=await topology.CreateExecutionPublisherAsync(new(id,"secret-projection",1),default);
        var logRecords=new List<string>(); var log=new SafeExecutionLog(logRecords.Add);
        foreach(var sentinel in new[] { oldSentinel,newSentinel })
        {
            current=sentinel;
            var receipt=await commands.RunNowAsync(job.JobId,Guid.NewGuid().ToString("N"),new ActorContext("synthetic","Anonymous",Guid.NewGuid()),default);
            var attempts=commands.GetAttemptStore(receipt.TargetId)!;
            await new QuartzPublisher(publisher,typeof(ExecuteTemplateJob)).PublishAsync(attempts.Intent,default);
            // This fixture's resolver acts as the child; no production parent/API accepts the vault.
            using var resolved=await vault.ResolveAsync(new SecretReference("http.credential"),default);
            Assert.Equal(sentinel,resolved.Reveal().ToString());
            await log.WriteAsync("TemplateCompleted",new Dictionary<string,string> { ["outcome"]="Success",["durationMs"]="1" },default);
            var quartz=(await publisher.GetJobDetail(QuartzPublisher.JobKey(attempts.Intent)))!.JobDataMap.ToDictionary(x=>x.Key,x=>x.Value);
            var records=JsonSerializer.Serialize(new { audits=commands.AuditEvents,revision=commands.GetRevision(job.JobId,1),execution=commands.GetExecution(receipt.TargetId),outbox=await commands.ReadPendingAsync(20,default),quartz,logs=logRecords })
                +await client.GetStringAsync($"/api/commands/{receipt.CommandId}")+await client.GetStringAsync($"/api/jobs/{job.JobId}/configuration");
            Assert.DoesNotContain(oldSentinel,records); Assert.DoesNotContain(newSentinel,records);
            Assert.DoesNotContain("credentialReference",JsonSerializer.Serialize(quartz));
        }
    }
}
