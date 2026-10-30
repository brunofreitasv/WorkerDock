using System.Text.Json;
using Microsoft.Data.SqlClient;
using WorkerDock.Application.Common;
using WorkerDock.Application.Dispatch;
using WorkerDock.Application.Executions;
using WorkerDock.Contracts.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Persistence;

namespace WorkerDock.IntegrationTests;

[Trait("Store", "Sql")]
public sealed class DispatchCapacitySqlTests
{
    private static readonly ActorContext Actor = new("manager", "Anonymous", Guid.NewGuid());

    private static async Task<TenantDatabaseOptions> Configure(TenantSqlFixture fixture)
    {
        await fixture.CreateAsync(false);
        await fixture.Runner.ApplyAsync("tenant", "0005_job_review_hold", default);
        var options = new TenantDatabaseOptions(fixture.RuntimeConnectionString, fixture.Database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        await SqlCapacityTestSetup.ReadyAsync(options);
        await fixture.Database.ExecuteAsync("UPDATE [workerdock].RuntimeStatus SET HostCeiling=10");
        return options;
    }

    private static async Task<(Guid Job, Guid Execution)> CreateRun(TenantSqlFixture fixture, TenantDatabaseOptions options,
        string overlap = "Allow", int limit = 1, bool pause = false)
    {
        var job = new JobRevision(Guid.NewGuid(), 0, "capacity", "workerdock.http", "1.1.0", JsonSerializer.SerializeToElement(new { }), null,
            new(null, 3, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120), 0, ["Busy"], false, overlap, 100, 5));
        var jobs = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        await jobs.SaveAsync(job, null, Actor, default);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1,ConcurrencyLimit={limit},PauseOnNeedsReview={(pause ? 1 : 0)} WHERE JobId='{job.JobId}'");
        var run = await jobs.RunNowAsync(job.JobId, Guid.NewGuid().ToString(), Actor, default);
        return (job.JobId, run.TargetId);
    }

    private static async Task<Guid> NextRun(TenantDatabaseOptions options, Guid job) =>
        (await new SqlJobCommands(options, JobCommandTestCatalog.Create()).RunNowAsync(job, Guid.NewGuid().ToString(), Actor, default)).TargetId;

    private static async Task<AttemptStart> Claim(TenantDatabaseOptions options, Guid execution, bool intent, Func<DispatchIntent, DispatchIntent>? alter = null)
    {
        var store = new SqlAttemptStore(options);
        if (!intent) return await store.BeginAsync(execution, "worker", default);
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.AvailableAtUtc,e.JobRevision,COALESCE((SELECT MAX(AttemptNumber) FROM [workerdock].Attempts a WHERE a.ExecutionId=e.ExecutionId),0)+1 FROM [workerdock].Executions e WHERE e.ExecutionId=@id";
        command.Parameters.AddWithValue("@id", execution);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var delivery = new DispatchIntent(Guid.NewGuid(), execution, reader.GetInt32(2), "workerdock.http",
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)), 5, reader.GetInt64(1), "");
        delivery = alter?.Invoke(delivery) ?? delivery;
        return await store.BeginAsync(delivery with { PayloadDigest = alter is null || delivery.PayloadDigest != "invalid" ? delivery.ComputeDigest() : "invalid" }, "worker", default);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Legacy_template_capacity_does_not_block_distinct_jobs(int legacyLimit, bool intent)
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options);
        var second = await CreateRun(fixture, options);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].TemplateCatalog SET ConcurrencyLimit={legacyLimit}");
        var ready = await new SqlExecutionQueue(options).ReadReadyAsync(10, default);
        Assert.Contains(first.Execution, ready);
        Assert.Contains(second.Execution, ready);
        Assert.Equal("Started", (await Claim(options, first.Execution, intent)).Decision);
        Assert.Equal("Started", (await Claim(options, second.Execution, intent)).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allow_job_capacity_and_reduced_limits_are_authoritative(bool intent)
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options, limit: 2);
        var second = await NextRun(options, first.Job);
        var third = await NextRun(options, first.Job);
        Assert.Equal("Started", (await Claim(options, first.Execution, intent)).Decision);
        Assert.Equal("Started", (await Claim(options, second, intent)).Decision);
        Assert.Equal("Held", (await Claim(options, third, intent)).Decision);
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Jobs SET ConcurrencyLimit=1 WHERE JobId='{first.Job}'");
        Assert.Equal("Held", (await Claim(options, third, intent)).Decision);
        Assert.Equal(2, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Attempts WHERE State=N'Running'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_claims_cannot_take_the_last_host_or_job_slot_twice(bool sameJob)
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options);
        var second = sameJob ? await NextRun(options, first.Job) : (await CreateRun(fixture, options)).Execution;
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].RuntimeStatus SET HostCeiling={(sameJob ? 10 : 1)}");
        var claims = await Task.WhenAll(Claim(options, first.Execution, false), Claim(options, second, true));
        Assert.Single(claims, x => x.Decision == "Started");
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Attempts WHERE State=N'Running'"));
    }

    [Theory]
    [InlineData("Queue", true, false)]
    [InlineData("Queue", false, true)]
    [InlineData("Skip", true, false)]
    [InlineData("Skip", false, true)]
    [InlineData("Allow", true, true)]
    public async Task Review_hold_obeys_live_switch_and_does_not_stall_another_job(string overlap, bool pause, bool starts)
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options, overlap, pause: pause);
        var store = new SqlAttemptStore(options);
        var lease = (await Claim(options, first.Execution, false)).Lease!;
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Attempts SET LeaseExpiresUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE AttemptId='{lease.AttemptId}'");
        Assert.Equal(1, await store.ReviewExpiredAsync(10, default));
        var next = await NextRun(options, first.Job);
        var other = await CreateRun(fixture, options);
        var ready = await new SqlExecutionQueue(options).ReadReadyAsync(10, default);
        Assert.Contains(other.Execution, ready);
        Assert.Equal(starts, ready.Contains(next));
        Assert.Equal(starts ? "Started" : "Held", (await Claim(options, next, false)).Decision);
        Assert.Equal("Started", (await Claim(options, other.Execution, true)).Decision);
        Assert.Equal(0, await fixture.Database.ScalarAsync($"SELECT HoldsJobSlot FROM [workerdock].Executions WHERE ExecutionId='{first.Execution}'"));
        if (overlap == "Skip" && pause)
            Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].OperationalEvents WHERE ExecutionId='{next}' AND Code=N'SkippedReviewHold'"));
    }

    [Fact]
    public async Task Retry_waiting_releases_capacity_and_later_queue_execution_can_start()
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options, "Queue");
        var lease = (await Claim(options, first.Execution, false)).Lease!;
        await new SqlAttemptStore(options).CompleteAsync(lease, new(new TemplateResult("TransientFailure", "Busy", false,
            JsonSerializer.SerializeToElement(new { }), TimeSpan.FromMinutes(2)), "Completed", true, false), default);
        var next = await NextRun(options, first.Job);
        Assert.Contains(next, await new SqlExecutionQueue(options).ReadReadyAsync(10, default));
        Assert.Equal("Started", (await Claim(options, next, true)).Decision);
        Assert.Equal(0, await fixture.Database.ScalarAsync($"SELECT HoldsJobSlot FROM [workerdock].Executions WHERE ExecutionId='{first.Execution}'"));
    }
    private static async Task<Guid> SeedQueued(TenantSqlFixture fixture, Guid source)
    {
        var id = Guid.NewGuid();
        await fixture.Database.ExecuteAsync($"INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc) SELECT '{id}',JobId,JobRevision,N'Manual',CONVERT(nvarchar(100),NEWID()),N'Queued',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME() FROM [workerdock].Executions WHERE ExecutionId='{source}'");
        return id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_skip_settlement_continues_after_bounded_admission_even_when_host_is_full(bool intent)
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options, "Skip", pause: true);
        var lease = (await Claim(options, first.Execution, false)).Lease!;
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Attempts SET LeaseExpiresUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE AttemptId='{lease.AttemptId}'");
        await new SqlAttemptStore(options).ReviewExpiredAsync(10, default);
        var waiting = new List<Guid>();
        for (var i = 0; i < 40; i++) waiting.Add(await SeedQueued(fixture, first.Execution));
        await new JobAdmission(options).AdmitAsync(first.Job, default);
        var other = await CreateRun(fixture, options);
        Assert.Equal("Started", (await Claim(options, other.Execution, false)).Decision);
        await fixture.Database.ExecuteAsync("UPDATE [workerdock].RuntimeStatus SET HostCeiling=1");
        foreach (var execution in waiting.Skip(32))
        {
            Assert.Equal("Held", (await Claim(options, execution, intent)).Decision);
            Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].OperationalEvents WHERE ExecutionId='{execution}' AND Code=N'SkippedReviewHold'"));
        }
        Assert.Equal(1, await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM [workerdock].Attempts WHERE State=N'Running'"));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("template")]
    [InlineData("due")]
    [InlineData("ordinal")]
    public async Task Invalid_intent_cannot_mutate_a_review_held_skip(string field)
    {
        await using var fixture = new TenantSqlFixture();
        var options = await Configure(fixture);
        var first = await CreateRun(fixture, options, "Skip", pause: true);
        var lease = (await Claim(options, first.Execution, false)).Lease!;
        await fixture.Database.ExecuteAsync($"UPDATE [workerdock].Attempts SET LeaseExpiresUtc=DATEADD(second,-1,SYSUTCDATETIME()) WHERE AttemptId='{lease.AttemptId}'");
        await new SqlAttemptStore(options).ReviewExpiredAsync(10, default);
        var next = await SeedQueued(fixture, first.Execution);
        var error = await Assert.ThrowsAsync<WorkerDockException>(() => Claim(options, next, true, delivery => field switch
        {
            "digest" => delivery with { PayloadDigest = "invalid" },
            "revision" => delivery with { EntityRevision = delivery.EntityRevision + 1 },
            "template" => delivery with { TemplateId = "invalid" },
            "due" => delivery with { DueUtc = delivery.DueUtc.AddSeconds(1) },
            _ => delivery with { AttemptOrdinal = delivery.AttemptOrdinal + 1 }
        }));
        Assert.Equal("DispatchIntegrityError", error.Code);
        Assert.Equal(1, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{next}' AND State=N'Queued'"));
        Assert.Equal(0, await fixture.Database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].OperationalEvents WHERE ExecutionId='{next}' AND Code LIKE N'Skipped%'"));
    }

}
