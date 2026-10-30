using WorkerDock.Application.Monitoring;
using WorkerDock.Infrastructure.Monitoring;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Application.Common;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Jobs;
using System.Text.Json;

namespace WorkerDock.IntegrationTests;

public sealed class MonitoringJobsSqlTests
{
    [Trait("Store", "Sql")]
    [Fact]
    public async Task JobsAsync_reads_scheduled_and_manual_rows()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var now = DateTimeOffset.UtcNow;
        var manualJob = await SeedJobAsync(database, "Manual monitoring job", now);
        var scheduledJob = await SeedJobAsync(database, "Scheduled monitoring job", now);
        await SeedScheduleAsync(database, scheduledJob, paused: false);

        var query = new SqlMonitoringQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), TimeProvider.System);
        var page = await query.JobsAsync(new(now.AddDays(-1), now.AddDays(1), null, null, null), new(null, 20), default);

        Assert.Collection(page.Items.OrderBy(x => x.Name),
            job => { Assert.Equal(manualJob, job.JobId); Assert.False(job.HasSchedule); Assert.False(job.SchedulePaused); },
            job => { Assert.Equal(scheduledJob, job.JobId); Assert.True(job.HasSchedule); Assert.False(job.SchedulePaused); });
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task JobsAsync_reads_paused_schedule()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var now = DateTimeOffset.UtcNow;
        var jobId = await SeedJobAsync(database, "Paused monitoring job", now);
        await SeedScheduleAsync(database, jobId, paused: true);

        var query = new SqlMonitoringQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), TimeProvider.System);
        var page = await query.JobsAsync(new(now.AddDays(-1), now.AddDays(1), null, null, null), new(null, 20), default);

        var item = Assert.Single(page.Items);
        Assert.True(item.HasSchedule);
        Assert.True(item.SchedulePaused);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task JobsAsync_can_read_jobs_older_than_the_default_ui_window()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var now = DateTimeOffset.UtcNow;
        var jobId = await SeedJobAsync(database, "Older monitoring job", now.AddDays(-30));

        var query = new SqlMonitoringQueries(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), TimeProvider.System);
        var page = await query.JobsAsync(new(now.AddDays(-7), now, null, null, null, ApplyDateFilter: false), new(null, 20), default);

        Assert.Contains(page.Items, job => job.JobId == jobId);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task ExecutionAsync_projects_current_wait_reason_from_live_capacity_and_review_state()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        await database.ApplyLatestSchemaAsync();
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        await JobCommandTestCatalog.RegisterSqlAsync(options);
        await database.ExecuteAsync("UPDATE [workerdock].RuntimeStatus SET Ready=1,HostCeiling=10,LastHeartbeatUtc=SYSUTCDATETIME() WHERE StateId=1");
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        var actor = new ActorContext("operator", "Anonymous", Guid.NewGuid());

        async Task<(Guid JobId, Guid ExecutionId)> Create(string name, string overlap, int capacity = 1, bool pause = false)
        {
            var id = Guid.NewGuid();
            var revision = new JobRevision(id, 0, name, "workerdock.http", "1.1.0", JsonSerializer.SerializeToElement(new { }), null,
                new(null, 2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), 0, ["Busy"], false, overlap, 100, 5));
            await commands.SaveAsync(revision, null, actor, default);
            await database.ExecuteAsync($"UPDATE [workerdock].Jobs SET Enabled=1,ConcurrencyLimit={capacity},PauseOnNeedsReview={(pause ? 1 : 0)} WHERE JobId='{id}'");
            var run = await commands.RunNowAsync(id, Guid.NewGuid().ToString(), actor, default);
            return (id, run.TargetId);
        }
        async Task SetState(Guid id, string state, bool running = false)
        {
            await database.ExecuteAsync($"UPDATE [workerdock].Executions SET State=N'{state}' WHERE ExecutionId='{id}';" +
                (running ? $" INSERT [workerdock].Attempts(AttemptId,ExecutionId,AttemptNumber,WorkerBootId,State,LeaseExpiresUtc,StartedAtUtc) VALUES(NEWID(),'{id}',1,N'test',N'Running',DATEADD(hour,1,SYSUTCDATETIME()),SYSUTCDATETIME());" : ""));
        }
        var jobsAtCapacity = await Create("job-capacity", "Allow");
        var jobWaiting = await commands.RunNowAsync(jobsAtCapacity.JobId, Guid.NewGuid().ToString(), actor, default);
        await SetState(jobsAtCapacity.ExecutionId, "Running", running: true);
        await database.ExecuteAsync("UPDATE [workerdock].TemplateCatalog SET ConcurrencyLimit=0");
        var query = new SqlMonitoringQueries(options, TimeProvider.System);
        Assert.Equal("JobCapacity", (await query.ExecutionAsync(jobWaiting.TargetId, default)).WaitReason);

        await database.ExecuteAsync("UPDATE [workerdock].RuntimeStatus SET HostCeiling=1 WHERE StateId=1");
        var hostWaiting = await Create("host-capacity", "Allow");
        Assert.Equal("HostCapacity", (await query.ExecutionAsync(hostWaiting.ExecutionId, default)).WaitReason);
        await database.ExecuteAsync("UPDATE [workerdock].RuntimeStatus SET HostCeiling=10 WHERE StateId=1");

        var review = await Create("review-hold", "Queue", pause: true);
        var reviewWaiting = await commands.RunNowAsync(review.JobId, Guid.NewGuid().ToString(), actor, default);
        await SetState(review.ExecutionId, "NeedsReview");
        Assert.Equal("ReviewHold", (await query.ExecutionAsync(reviewWaiting.TargetId, default)).WaitReason);

        var retryReview = await Create("retry-review-hold", "Queue", pause: true);
        await SetState(retryReview.ExecutionId, "NeedsReview");
        var dueRetry = await commands.RunNowAsync(retryReview.JobId, Guid.NewGuid().ToString(), actor, default);
        await database.ExecuteAsync($"UPDATE [workerdock].Executions SET State=N'RetryWaiting',AvailableAtUtc=SYSUTCDATETIME() WHERE ExecutionId='{dueRetry.TargetId}'");
        Assert.Equal("ReviewHold", (await query.ExecutionAsync(dueRetry.TargetId, default)).WaitReason);

        var retrySkipReview = await Create("retry-skip-review-hold", "Skip", pause: true);
        await SetState(retrySkipReview.ExecutionId, "NeedsReview");
        await database.ExecuteAsync($"UPDATE [workerdock].Jobs SET PauseOnNeedsReview=0 WHERE JobId='{retrySkipReview.JobId}'");
        var retrySkip = await commands.RunNowAsync(retrySkipReview.JobId, Guid.NewGuid().ToString(), actor, default);
        await database.ExecuteAsync($"UPDATE [workerdock].Executions SET State=N'RetryWaiting',AvailableAtUtc=SYSUTCDATETIME() WHERE ExecutionId='{retrySkip.TargetId}'; UPDATE [workerdock].Jobs SET PauseOnNeedsReview=1 WHERE JobId='{retrySkipReview.JobId}'");
        Assert.Equal("ReviewHold", (await query.ExecutionAsync(retrySkip.TargetId, default)).WaitReason);

        var skipReview = await Create("review-skip", "Skip", pause: true);
        await SetState(skipReview.ExecutionId, "NeedsReview");
        await database.ExecuteAsync($"UPDATE [workerdock].Jobs SET PauseOnNeedsReview=0 WHERE JobId='{skipReview.JobId}'");
        var willSkip = await commands.RunNowAsync(skipReview.JobId, Guid.NewGuid().ToString(), actor, default);
        await database.ExecuteAsync($"UPDATE [workerdock].Executions SET State=N'Queued',HoldsJobSlot=0 WHERE ExecutionId='{willSkip.TargetId}'; UPDATE [workerdock].Jobs SET PauseOnNeedsReview=1 WHERE JobId='{skipReview.JobId}'");
        Assert.Equal("ReviewSkip", (await query.ExecutionAsync(willSkip.TargetId, default)).WaitReason);
        await new WorkerDock.Infrastructure.Dispatch.JobAdmission(options).AdmitAsync(skipReview.JobId, default);
        var skipped = await query.ExecutionAsync(willSkip.TargetId, default);
        Assert.Equal("Skipped", skipped.State);
        Assert.Null(skipped.WaitReason);
        Assert.Contains(skipped.Timeline, item => item.State == "SkippedReviewHold");

        var queueBehindAllow = await Create("queue-behind-allow", "Allow", capacity: 3);
        await SetState(queueBehindAllow.ExecutionId, "Running", running: true);
        await SetPolicy(database, queueBehindAllow.JobId, "Queue");
        var queueBehindRunning = await commands.RunNowAsync(queueBehindAllow.JobId, Guid.NewGuid().ToString(), actor, default);
        var queueBehindAllowDetail = await query.ExecutionAsync(queueBehindRunning.TargetId, default);
        Assert.Equal("JobRunning", queueBehindAllowDetail.WaitReason);
        Assert.NotEqual(queueBehindAllowDetail.JobRevision, (await query.ExecutionAsync(queueBehindAllow.ExecutionId, default)).JobRevision);

        var allowBehindQueue = await Create("allow-behind-queue", "Queue", capacity: 3);
        await SetPolicy(database, allowBehindQueue.JobId, "Allow");
        var allowBehindWaiting = await commands.RunNowAsync(allowBehindQueue.JobId, Guid.NewGuid().ToString(), actor, default);
        var allowBehindQueueDetail = await query.ExecutionAsync(allowBehindWaiting.TargetId, default);
        Assert.Equal("EarlierQueued", allowBehindQueueDetail.WaitReason);
        Assert.NotEqual(allowBehindQueueDetail.JobRevision, (await query.ExecutionAsync(allowBehindQueue.ExecutionId, default)).JobRevision);

        var allowBehindSkip = await Create("allow-behind-skip", "Skip", capacity: 3);
        await SetPolicy(database, allowBehindSkip.JobId, "Allow");
        var allowBehindSkipWaiting = await commands.RunNowAsync(allowBehindSkip.JobId, Guid.NewGuid().ToString(), actor, default);
        var allowBehindSkipDetail = await query.ExecutionAsync(allowBehindSkipWaiting.TargetId, default);
        Assert.Equal("EarlierQueued", allowBehindSkipDetail.WaitReason);
        Assert.NotEqual(allowBehindSkipDetail.JobRevision, (await query.ExecutionAsync(allowBehindSkip.ExecutionId, default)).JobRevision);

        var eligibleAllow = await Create("eligible-allow", "Allow", capacity: 2);
        var eligibleAllowSecond = await commands.RunNowAsync(eligibleAllow.JobId, Guid.NewGuid().ToString(), actor, default);
        Assert.Null((await query.ExecutionAsync(eligibleAllowSecond.TargetId, default)).WaitReason);
        await SetState(eligibleAllowSecond.TargetId, "Succeeded");
        Assert.Null((await query.ExecutionAsync(eligibleAllowSecond.TargetId, default)).WaitReason);
    }

    private static async Task SetPolicy(SqlDatabaseFixture database, Guid jobId, string overlap)
    {
        await database.ExecuteAsync($"""
            DECLARE @revision bigint=(SELECT CurrentRevision FROM [workerdock].Jobs WHERE JobId='{jobId}');
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
                SELECT JobId,@revision+1,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,JSON_MODIFY(PolicyJson,'$.Overlap',N'{overlap}'),SYSUTCDATETIME()
                FROM [workerdock].JobRevisions WHERE JobId='{jobId}' AND Revision=@revision;
            UPDATE [workerdock].Jobs SET CurrentRevision=@revision+1 WHERE JobId='{jobId}';
            """);
    }

    private static async Task<Guid> SeedJobAsync(SqlDatabaseFixture database, string name, DateTimeOffset createdAt)
    {
        var jobId = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{jobId}',NULL,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc)
            VALUES('{jobId}',1,N'{name.Replace("'", "''")}',N'workerdock.sql-server',N'1.1.0',NCHAR(123)+NCHAR(125),NULL,NCHAR(123)+N'"timeout":null'+NCHAR(125),'{createdAt.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}');
            UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId='{jobId}';
            """);
        return jobId;
    }

    private static async Task SeedScheduleAsync(SqlDatabaseFixture database, Guid jobId, bool paused)
    {
        var scheduleId = Guid.NewGuid();
        await database.ExecuteAsync($"""
            INSERT [workerdock].Schedules(ScheduleId,JobId,Revision,Kind,Cron,ZoneId,OneTimeUtc,ActivatedUtc,Paused,PausedAtUtc,CursorUtc,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs,ZoneRulesFingerprint)
            VALUES('{scheduleId}','{jobId}',1,N'Cron',N'0 9 * * *',N'UTC',NULL,SYSUTCDATETIME(),{(paused ? 1 : 0)},{(paused ? "SYSUTCDATETIME()" : "NULL")},SYSUTCDATETIME(),N'Skip',60000,0,0,REPLICATE('0',64));
            """);
    }
}
