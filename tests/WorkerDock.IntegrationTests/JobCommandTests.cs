using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using WorkerDock.Application.Common;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;

namespace WorkerDock.IntegrationTests;

public sealed class JobCommandTests
{
    private static readonly ActorContext Actor = new("issuer:subject", "Entra", Guid.NewGuid());

    [Fact]
    public async Task Stale_edit_returns_412_without_revision_or_audit()
    {
        var service = new InMemoryJobCommands(TimeProvider.System);
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
    public async Task Repeated_manual_key_returns_original_for_24_hours()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
        var job = Job("Scheduled");
        var service = new InMemoryJobCommands(clock, [job]);
        var requests = await Task.WhenAll(
            service.RunNowAsync(job.JobId, "same-key", Actor, default).AsTask(),
            service.RunNowAsync(job.JobId, "same-key", Actor, default).AsTask());
        Assert.Equal(requests[0], requests[1]);
        Assert.Equal(1, service.ExecutionCount);
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
        var service = new InMemoryJobCommands(TimeProvider.System, [job]);
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
        var service = new InMemoryJobCommands(TimeProvider.System, [enabled]);
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
        var service = new InMemoryJobCommands(TimeProvider.System, archivedSeeds: [archived]);
        var error = await Assert.ThrowsAsync<WorkerDockException>(async () =>
            await service.RunNowAsync(archived.JobId, "request", Actor, default));
        Assert.Equal("JobArchived", error.Code);
        Assert.Equal(0, service.ExecutionCount);
    }

    [Fact]
    public async Task Execution_pins_immutable_job_revision()
    {
        var job = Job("Original");
        var service = new InMemoryJobCommands(TimeProvider.System, [job]);
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
            part == phase ? throw new InvalidOperationException("injected") : ValueTask.CompletedTask);
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
        await using var host = new AccessHost();
        using var client = host.Client();
        var token = (await client.GetFromJsonAsync<CsrfView>("/api/antiforgery"))!.RequestToken;
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(Job("HTTP job") with { JobId = Guid.Empty })
        };
        create.Headers.Add("Origin", "https://localhost");
        create.Headers.Add("X-WorkerDock-CSRF", token);
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
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
