using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Templates;
using Microsoft.Extensions.DependencyInjection;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

public sealed class JobCommandTests
{
    private static readonly ActorContext Actor = new("issuer:subject", "Entra", Guid.NewGuid());

    [Fact]
    public async Task Trusted_registered_template_is_validated_before_save_enable_and_run()
    {
        var descriptor=TemplateCatalogTests.Descriptor("""{"type":"object","properties":{"count":{"type":"integer","minimum":1}},"additionalProperties":false}""") with { Id="custom.trusted" };
        descriptor=WorkerDock.Infrastructure.Templates.TemplateMetadata.WithDigest(descriptor);
        var catalog=new WorkerDock.Infrastructure.Templates.TemplateCatalog([descriptor]); await catalog.RegisterAsync([descriptor],default);
        var commands=new InMemoryJobCommands(TimeProvider.System,templateCatalog:catalog);
        var invalid=Job("invalid") with { TemplateId=descriptor.Id,TemplateVersion=descriptor.Version,Configuration=JsonDocument.Parse("""{"count":0}""").RootElement.Clone() };
        await commands.SaveAsync(invalid,null,Actor,default);
        var editor=await commands.GetEditableAsync(invalid.JobId,default);
        Assert.False(editor.Enabled);
        var validation=await Assert.ThrowsAsync<WorkerDockException>(async()=>await commands.EnableManualAsync(invalid.JobId,editor.Version,Actor,default));
        Assert.Equal("ValidationFailed",validation.Code);
        var stale=await Assert.ThrowsAsync<WorkerDockException>(async()=>await commands.EnableManualAsync(invalid.JobId,BitConverter.GetBytes(9L),Actor,default));
        Assert.Equal("StaleVersion",stale.Code);
        var valid=invalid with { Configuration=JsonDocument.Parse("""{"count":2}""").RootElement.Clone() };
        await commands.SaveAsync(valid,editor.Version,Actor,default);
        var updated=await commands.GetEditableAsync(valid.JobId,default);
        await commands.EnableManualAsync(valid.JobId,updated.Version,Actor,default);
        Assert.True((await commands.GetEditableAsync(valid.JobId,default)).Enabled);
        var run=await commands.RunNowAsync(valid.JobId,"custom-run",Actor,default);
        Assert.Equal(valid.JobId,commands.GetExecution(run.TargetId)?.JobId);
    }

    [Fact]
    public async Task Missing_template_catalog_fails_closed_before_job_save_or_run()
    {
        var job=Job("catalog required");
        var commands=new InMemoryJobCommands(TimeProvider.System);
        var save=await Assert.ThrowsAsync<WorkerDockException>(async()=>await commands.SaveAsync(job,null,Actor,default));
        Assert.Equal("TemplateCatalogUnavailable",save.Code);

        var enabled=new InMemoryJobCommands(TimeProvider.System,[job]);
        var run=await Assert.ThrowsAsync<WorkerDockException>(async()=>await enabled.RunNowAsync(job.JobId,"no-catalog",Actor,default));
        Assert.Equal("TemplateCatalogUnavailable",run.Code);
        Assert.Equal(0,enabled.ExecutionCount);

        var sql=new SqlJobCommands(new TenantDatabaseOptions("",Guid.NewGuid()));
        var sqlSave=await Assert.ThrowsAsync<WorkerDockException>(async()=>await sql.SaveAsync(job,null,Actor,default));
        Assert.Equal("TemplateCatalogUnavailable",sqlSave.Code);
    }

    [Fact]
    public async Task Read_only_viewer_cannot_queue_run_now_even_with_valid_csrf_and_request_key()
    {
        var descriptor=WorkerDock.Infrastructure.Templates.TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object"}""") with { Id="workerdock.http" });
        var catalog=new TemplateCatalog([descriptor]); await catalog.RegisterAsync([descriptor],default);
        var job=Job("viewer denied run");
        var commands=new InMemoryJobCommands(TimeProvider.System,[job],templateCatalog:catalog);
        using var issuer=new LocalOidcIssuer("Viewer");
        await using var baseHost=new AccessHost(issuer,"Manager");
        await using var host=baseHost.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>{ services.AddSingleton<ITemplateCatalog>(catalog); services.AddSingleton<IJobCommands>(commands); }));
        using var client=host.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
        await issuer.SignIn(client);
        var token=(await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/jobs/{job.JobId}/runs") { Content=JsonContent.Create(new { requestKey="valid-viewer-key" }) };
        request.Headers.Add("Origin","https://localhost"); request.Headers.Add("X-WorkerDock-CSRF",token);
        using var response=await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        Assert.Equal(0,commands.ExecutionCount);
    }

    [Fact]
    public async Task Receipt_kind_survives_request_key_expiry_while_execution_is_still_queued()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow); var job = Job("long backlog");
        var commands = new InMemoryJobCommands(clock, [job], templateCatalog: JobCommandTestCatalog.Create());
        var first = await commands.RunNowAsync(job.JobId, "expires", Actor, default);
        clock.Advance(TimeSpan.FromHours(25));
        var second = await commands.RunNowAsync(job.JobId, "expires", Actor, default);
        Assert.NotEqual(first.CommandId, second.CommandId);
        var attempts = commands.GetAttemptStore(first.TargetId)!;
        await attempts.BeginAsync(attempts.Intent, "boot", default);
        Assert.Equal("Applied", (await commands.GetCommandAsync(first.CommandId, default)).State);
        Assert.Equal("Pending", (await commands.GetCommandAsync(second.CommandId, default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_and_stop_receipts_follow_authoritative_attempt_commits(bool stopFirst)
    {
        var job = Job("receipts");
        var commands = new InMemoryJobCommands(TimeProvider.System, [job], templateCatalog: JobCommandTestCatalog.Create());
        var run = await commands.RunNowAsync(job.JobId, "stable-key", Actor, default);
        Assert.Equal("Pending", (await commands.GetCommandAsync(run.CommandId, default)).State);
        var attempts = commands.GetAttemptStore(run.TargetId)!;
        var lease = (await attempts.BeginAsync(attempts.Intent, "boot", default)).Lease!;
        Assert.Equal("Applied", (await commands.GetCommandAsync(run.CommandId, default)).State);
        WorkerDock.Application.Common.CommandReceipt? stop = null;
        if (stopFirst)
        {
            stop = await commands.StopAsync(run.TargetId, false, "secret-audit-only", Actor, default);
            Assert.Equal("Pending", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        }
        await attempts.CompleteAsync(lease, new(new WorkerDock.Contracts.Templates.TemplateResult("Success", "Ok", false,
            JsonSerializer.SerializeToElement(new { }), null), "Completed", true, false), default);
        stop ??= await commands.StopAsync(run.TargetId, false, "secret-audit-only", Actor, default);
        Assert.Equal("Applied", (await commands.GetCommandAsync(stop.CommandId, default)).State);
        Assert.Equal(stopFirst ? "Stopped" : "Succeeded", attempts.Snapshot.State);
        var replay = await commands.RunNowAsync(job.JobId, "stable-key", Actor, default);
        Assert.Equal(run.CommandId, replay.CommandId);
        Assert.Equal("Applied", replay.State);
        Assert.Equal(1, commands.ExecutionCount);
        Assert.DoesNotContain("secret-audit-only", JsonSerializer.Serialize(await commands.GetCommandAsync(stop.CommandId, default)));
    }

    [Fact]
    public async Task Unconfirmed_stop_failure_and_unpublished_cancellation_settle_receipts()
    {
        var job = Job("receipt failures"); var commands = new InMemoryJobCommands(TimeProvider.System, [job], templateCatalog: JobCommandTestCatalog.Create());
        var run = await commands.RunNowAsync(job.JobId, "cancel", Actor, default);
        await commands.StopAsync(run.TargetId, false, "cancel before launch", Actor, default);
        Assert.Equal("Failed", (await commands.GetCommandAsync(run.CommandId, default)).State);
        var second = await commands.RunNowAsync(job.JobId, "running", Actor, default);
        var attempts = commands.GetAttemptStore(second.TargetId)!;
        var lease = (await attempts.BeginAsync(attempts.Intent, "boot", default)).Lease!;
        var stop = await commands.StopAsync(second.TargetId, true, "terminate", Actor, default);
        await attempts.CompleteAsync(lease, new(null, "ProcessFailed", false, true), default);
        Assert.Equal("NeedsReview", attempts.Snapshot.State);
        Assert.Equal("Failed", (await commands.GetCommandAsync(stop.CommandId, default)).State);
    }

    [Theory]
    [InlineData(65500, false)]
    [InlineData(54000, true)]
    [InlineData(30000, false)]
    public async Task Save_bounds_the_serialized_execution_envelope(int length, bool accepted)
    {
        var job = Job("bounded") with { Configuration = JsonDocument.Parse("{\"value\":\"" + new string(length == 30000 ? '<' : 'x', length) + "\"}").RootElement.Clone() };
        var commands = new InMemoryJobCommands(TimeProvider.System, templateCatalog: JobCommandTestCatalog.Create());
        if (accepted) await commands.SaveAsync(job, null, Actor, default);
        else
        {
            var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.SaveAsync(job, null, Actor, default));
            Assert.Equal("ValidationFailed", error.Code);
            Assert.Equal(0, commands.AuditCount);
            Assert.Equal(0, commands.ExecutionCount);
        }
    }

    [Fact]
    public async Task Stale_edit_returns_412_without_revision_or_audit()
    {
        var service = new InMemoryJobCommands(TimeProvider.System, templateCatalog: JobCommandTestCatalog.Create());
        var draft = Job("First");
        var created = await service.SaveAsync(draft, null, Actor, default);
        var original = await service.GetEditableAsync(draft.JobId, default);
        Assert.False(original.Enabled);
        Assert.False(original.Archived);
        Assert.Equal(1, original.Revision.Revision);
        await service.SaveAsync(draft with { Name = "Editor one" }, original.Version, Actor, default);
        var auditBefore = service.AuditCount;
        var outboxBefore = service.OutboxCount;
        var stale = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.SaveAsync(draft with { Name = "Editor two" }, original.Version, Actor, default));
        Assert.Equal("StaleVersion", stale.Code);
        Assert.Equal(auditBefore, service.AuditCount);
        Assert.Equal(outboxBefore, service.OutboxCount);
        Assert.Equal(2, (await service.GetEditableAsync(draft.JobId, default)).Revision.Revision);
        Assert.Equal(created, await service.GetCommandAsync(created.CommandId, default));
    }

    [Fact]
    public async Task Revision_audit_identifies_transition_and_outcome_without_configuration()
    {
        var service = new InMemoryJobCommands(TimeProvider.System, templateCatalog: JobCommandTestCatalog.Create());
        var draft = Job("First") with { Configuration = JsonDocument.Parse("{\"secret\":\"audit-must-not-copy-this\"}").RootElement.Clone() };
        await service.SaveAsync(draft, null, Actor, default);
        var editor = await service.GetEditableAsync(draft.JobId, default);
        await service.SaveAsync(draft with { Name = "Second" }, editor.Version, Actor, default);

        var events = service.AuditEvents;
        Assert.Equal(2, events.Length);
        Assert.Null(events[0].BeforeRevision);
        Assert.Equal(1, events[0].AfterRevision);
        Assert.Equal(1, events[1].BeforeRevision);
        Assert.Equal(2, events[1].AfterRevision);
        Assert.All(events, entry =>
        {
            Assert.Equal("Applied", entry.Outcome);
            Assert.Equal(Actor.Subject, entry.ActorSubject);
            Assert.Equal(Actor.AuthenticationMode, entry.AuthenticationMode);
            Assert.Equal(Actor.CorrelationId, entry.CorrelationId);
        });
        Assert.DoesNotContain("audit-must-not-copy-this", JsonSerializer.Serialize(events));
    }

    [Fact]
    public async Task Repeated_manual_key_returns_original_for_24_hours()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
        var job = Job("Scheduled");
        var service = new InMemoryJobCommands(clock, [job], templateCatalog: JobCommandTestCatalog.Create());
        var requests = await Task.WhenAll(
            service.RunNowAsync(job.JobId, "same-key", Actor, default).AsTask(),
            service.RunNowAsync(job.JobId, "same-key", Actor, default).AsTask());
        Assert.Equal(requests[0], requests[1]);
        Assert.Equal(1, service.ExecutionCount);
        var audit = Assert.Single(service.AuditEvents);
        Assert.Equal("Accepted", audit.Outcome);
        Assert.Equal(1, audit.BeforeRevision);
        Assert.Equal(1, audit.AfterRevision);
        Assert.Equal(Actor.AuthenticationMode, audit.AuthenticationMode);
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.Equal(requests[0], await service.RunNowAsync(job.JobId, "same-key", Actor, default));
        clock.Advance(TimeSpan.FromMinutes(2));
        var afterWindow = await service.RunNowAsync(job.JobId, "same-key", Actor, default);
        Assert.NotEqual(requests[0].TargetId, afterWindow.TargetId);
        Assert.Equal(2, service.ExecutionCount);
    }

    [Fact]
    public async Task Repeated_key_returns_original_after_job_edit()
    {
        var job = Job("Original");
        var service = new InMemoryJobCommands(TimeProvider.System, [job], templateCatalog: JobCommandTestCatalog.Create());
        var first = await service.RunNowAsync(job.JobId, "retry-key", Actor, default);
        var editor = await service.GetEditableAsync(job.JobId, default);
        await service.SaveAsync(editor.Revision with { Name = "Edited" }, editor.Version, Actor, default);
        var retry = await service.RunNowAsync(job.JobId, "retry-key", Actor, default);
        Assert.Equal(first, retry);
        Assert.Equal(1, service.ExecutionCount);
        Assert.Equal(1, service.GetExecution(first.TargetId)?.JobRevision);
    }

    [Fact]
    public async Task Disabled_job_rejects_run_and_actor_scope_isolated()
    {
        var disabled = Job("Disabled");
        var enabled = Job("Enabled");
        var service = new InMemoryJobCommands(TimeProvider.System, [enabled], templateCatalog: JobCommandTestCatalog.Create());
        await service.SaveAsync(disabled, null, Actor, default);
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.RunNowAsync(disabled.JobId, "request", Actor, default));
        Assert.Equal("JobDisabled", error.Code);
        var first = await service.RunNowAsync(enabled.JobId, "request", Actor, default);
        var otherActor = await service.RunNowAsync(enabled.JobId, "request", new("issuer:other", "Entra", Guid.NewGuid()), default);
        var anonymous = await service.RunNowAsync(enabled.JobId, "request", new("session:opaque", "Anonymous", Guid.NewGuid()), default);
        Assert.NotEqual(first.TargetId, otherActor.TargetId);
        Assert.NotEqual(first.TargetId, anonymous.TargetId);
        Assert.Equal(3, service.ExecutionCount);
    }

    [Fact]
    public async Task Archived_job_rejects_manual_run()
    {
        var archived = Job("Archived");
        var service = new InMemoryJobCommands(TimeProvider.System, archivedSeeds: [archived], templateCatalog: JobCommandTestCatalog.Create());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.RunNowAsync(archived.JobId, "request", Actor, default));
        Assert.Equal("JobArchived", error.Code);
        Assert.Equal(0, service.ExecutionCount);
    }

    [Fact]
    public async Task Execution_pins_immutable_job_revision()
    {
        var job = Job("Original");
        var service = new InMemoryJobCommands(TimeProvider.System, [job], templateCatalog: JobCommandTestCatalog.Create());
        var first = await service.RunNowAsync(job.JobId, "first", Actor, default);
        var editable = await service.GetEditableAsync(job.JobId, default);
        await service.SaveAsync(job with { Name = "Updated" }, editable.Version, Actor, default);
        var second = await service.RunNowAsync(job.JobId, "second", Actor, default);
        Assert.Equal(1, service.GetExecution(first.TargetId)?.JobRevision);
        Assert.Equal(2, service.GetExecution(second.TargetId)?.JobRevision);
        Assert.Equal("Original", service.GetRevision(job.JobId, 1)?.Name);
        Assert.Equal("Updated", service.GetRevision(job.JobId, 2)?.Name);
        Assert.NotEqual(first.TargetId, second.TargetId);
    }

    [Theory]
    [InlineData("Audit")]
    [InlineData("Outbox")]
    public async Task Failed_audit_or_outbox_prevents_job_mutation(string phase)
    {
        var job = Job("Uncommitted");
        var service = new InMemoryJobCommands(TimeProvider.System, phase == "Outbox" ? [job] : null, beforeCommit: (part, _) =>
            part == phase ? throw new InvalidOperationException("injected") : ValueTask.CompletedTask, templateCatalog: JobCommandTestCatalog.Create());
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
        {
            if (phase == "Audit") await service.SaveAsync(job, null, Actor, default);
            else await service.RunNowAsync(job.JobId, "request", Actor, default);
        });
        Assert.Equal("PersistenceUnavailable", error.Code);
        Assert.Equal(0, service.AuditCount);
        Assert.Equal(0, service.OutboxCount);
        Assert.Equal(0, service.ExecutionCount);
        if (phase == "Audit")
        {
            var missing = await Assert.ThrowsAsync<WorkerDockException>(async () =>
                await service.GetEditableAsync(job.JobId, default));
            Assert.Equal("JobNotFound", missing.Code);
        }
    }

    [Fact]
    public async Task Management_routes_return_safe_receipts_and_stale_edit_problem()
    {
        var descriptor=WorkerDock.Infrastructure.Templates.TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object"}""") with { Id="workerdock.http" });
        var catalog=new TemplateCatalog([descriptor]); await catalog.RegisterAsync([descriptor],default);
        await using var baseHost = new AccessHost();
        await using var host=baseHost.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>services.AddSingleton<ITemplateCatalog>(catalog)));
        using var client = host.CreateClient(new() { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
        var token = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(Job("HTTP job") with { JobId = Guid.Empty })
        };
        create.Headers.Add("Origin", "https://localhost");
        create.Headers.Add("X-WorkerDock-CSRF", token);
        using var created = await client.SendAsync(create);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var receipt = await created.Content.ReadFromJsonAsync<CommandReceipt>();
        Assert.NotNull(receipt);
        Assert.NotEqual(Guid.Empty, receipt.TargetId);
        Assert.Equal("Applied", receipt.State);
        Assert.Equal(receipt, await client.GetFromJsonAsync<CommandReceipt>($"/api/commands/{receipt.CommandId}"));
        using var firstGet = await client.GetAsync($"/api/jobs/{receipt.TargetId}/configuration");
        Assert.Equal(HttpStatusCode.OK, firstGet.StatusCode);
        Assert.NotNull(firstGet.Headers.ETag);
        var editor = await firstGet.Content.ReadFromJsonAsync<WorkerDock.Application.Jobs.JobEditorDto>();
        Assert.False(editor!.Enabled);
        using var edit = new HttpRequestMessage(HttpMethod.Put, $"/api/jobs/{receipt.TargetId}")
        {
            Content = JsonContent.Create(editor.Revision with { Name = "Edited" })
        };
        edit.Headers.Add("Origin", "https://localhost"); edit.Headers.Add("X-WorkerDock-CSRF", token);
        edit.Headers.IfMatch.Add(firstGet.Headers.ETag!);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(edit)).StatusCode);
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/jobs/{receipt.TargetId}")
        {
            Content = JsonContent.Create(editor.Revision with { Name = "Stale" })
        };
        stale.Headers.Add("Origin", "https://localhost"); stale.Headers.Add("X-WorkerDock-CSRF", token);
        stale.Headers.IfMatch.Add(firstGet.Headers.ETag!);
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        using var problem = JsonDocument.Parse(await staleResponse.Content.ReadAsStringAsync());
        Assert.Equal("StaleVersion", problem.RootElement.GetProperty("code").GetString());
        using var run = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{receipt.TargetId}/runs")
        {
            Content = JsonContent.Create(new { requestKey = "manual-key" })
        };
        run.Headers.Add("Origin", "https://localhost"); run.Headers.Add("X-WorkerDock-CSRF", token);
        using var disabled = await client.SendAsync(run);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
    }

    private static JobRevision Job(string name) => new(Guid.NewGuid(), 0, name, "workerdock.http", "1.0.0",
        JsonDocument.Parse("{}").RootElement.Clone(), null,
        new(null, 3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 0.1, ["Transient"], false, "Queue", 10, 5));

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
