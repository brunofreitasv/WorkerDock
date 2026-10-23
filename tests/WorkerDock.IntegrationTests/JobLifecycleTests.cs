using Microsoft.Data.SqlClient;
using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Values;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Scheduling;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Infrastructure.Connections;
using WorkerDock.Infrastructure.Workers;
using WorkerDock.Infrastructure.Dispatch;
using WorkerDock.Domain.Jobs;
using WorkerDock.Quartz.Scheduling;
using WorkerDock.Templates.Http;

namespace WorkerDock.IntegrationTests;

public sealed class JobLifecycleTests
{
    private static readonly WorkerDock.Application.Common.ActorContext Actor = new("operator", "Anonymous", Guid.NewGuid());

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Enable_rejects_scheduled_timestamp_binding_when_job_has_no_schedule()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var descriptor = HttpTemplateDescriptors.Get("1.1.0");
        var catalog = new TemplateCatalog([descriptor]);
        await catalog.RegisterAsync([descriptor], default);
        await database.ExecuteAsync($"INSERT [workerdock].TemplateCatalog(TemplateId,TemplateVersion,MetadataDigest,Enabled) VALUES(N'workerdock.http',N'1.1.0',N'{descriptor.MetadataDigest}',1)");
        var jobId = Guid.NewGuid();
        var plan = DynamicValueJson.Write(new(1, "UTC", [new("/parameters/when", DynamicValueSource.ScheduledTimestamp, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoUtcTimestamp)]));
        var configuration = JsonSerializer.SerializeToElement(new
        {
            method = "GET", url = "https://example.test", query = new { }, headers = new { }, secretHeaders = new { },
            parameters = new { when = "fixed" }, successStatuses = Array.Empty<int>(), maxResponseBytes = 1024, responseMode = "SafeSummary"
        });
        var policy = "{\"Timeout\":null,\"MaxAttempts\":1,\"RetryBaseDelay\":\"00:00:00\",\"RetryMaxDelay\":\"00:00:00\",\"JitterRatio\":0,\"RetryableCodes\":[],\"ReplaySafe\":false,\"Overlap\":\"Queue\",\"BacklogLimit\":10,\"Priority\":5}";
        await database.ExecuteAsync($$"""
            INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{{jobId}}',NULL,0,0,1);
            INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc,DynamicValuesJson)
            VALUES('{{jobId}}',1,N'dynamic unscheduled',N'workerdock.http',N'1.1.0',N'{{configuration.GetRawText()}}',NULL,N'{{policy}}',SYSUTCDATETIME(),N'{{plan.GetRawText()}}');
            UPDATE [workerdock].Jobs SET CurrentRevision=1 WHERE JobId='{{jobId}}';
            """);
        var lifecycle = new SqlJobLifecycle(options, catalog, new QuartzScheduleEvaluator(), TimeProvider.System);

        var error = await Assert.ThrowsAsync<WorkerDockException>(async () => await lifecycle.ApplyAsync(jobId, "Enable", await VersionAsync(database, jobId), Actor, default));

        Assert.Equal("ValidationFailed", error.Code);
        Assert.Equal(0, await database.ScalarAsync($"SELECT Enabled FROM [workerdock].Jobs WHERE JobId='{jobId}'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Disable_stops_schedule_claims_without_projection()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var version = await VersionAsync(database, jobId);
        var lifecycle = new SqlJobLifecycle(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), null, new QuartzScheduleEvaluator(), TimeProvider.System);

        await lifecycle.ApplyAsync(jobId, "Disable", version, Actor, default);

        Assert.Equal(0, await database.ScalarAsync($"SELECT Enabled FROM [workerdock].Jobs WHERE JobId='{jobId}'"));
        Assert.Equal(2, await database.ScalarAsync($"SELECT Revision FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
        Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EntityId=(SELECT ScheduleId FROM [workerdock].Schedules WHERE JobId='{jobId}') AND EventType=N'ScheduleProjection'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Pause_suppresses_sweep_and_resume_materializes_future_work()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var lifecycle = new SqlJobLifecycle(options, null, new FiveFieldScheduleEvaluator(), TimeProvider.System);
        var sweep = new SqlScheduleSweep(options, new OccurrenceWriter(options, new FiveFieldScheduleEvaluator()));
        var initialCursor = DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime;
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ZoneId=N'Etc/UTC',ActivatedUtc='{initialCursor:yyyy-MM-ddTHH:mm:ss}',CursorUtc='{initialCursor:yyyy-MM-ddTHH:mm:ss}' WHERE ScheduleId='{scheduleId}'");

        var version = await VersionAsync(database, jobId);
        await lifecycle.ApplyAsync(jobId, "PauseSchedule", version, Actor, default);
        var pausedVersion = await VersionAsync(database, jobId);
        var pausedAt = await ScalarStringAsync(database, $"SELECT CONVERT(nvarchar(33),PausedAtUtc,126) FROM [workerdock].Schedules WHERE ScheduleId='{scheduleId}'");
        Assert.NotEqual(version, pausedVersion);
        Assert.Equal(0, await sweep.RunDueAsync(DateTimeOffset.UtcNow.AddMinutes(2), 10, default));
        Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}'"));

        await lifecycle.ApplyAsync(jobId, "ResumeSchedule", pausedVersion, Actor, default);
        Assert.NotEqual(pausedVersion, await VersionAsync(database, jobId));
        Assert.True(await sweep.RunDueAsync(DateTimeOffset.UtcNow.AddMinutes(3), 10, default) > 0);

        Assert.Equal(0, await database.ScalarAsync($"SELECT Paused FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
        Assert.Equal(3, await database.ScalarAsync($"SELECT Revision FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
        Assert.True(await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND ScheduledForUtc>CONVERT(datetime2,'{pausedAt}',126)") > 0);
        Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EntityId=(SELECT ScheduleId FROM [workerdock].Schedules WHERE JobId='{jobId}') AND EventType=N'ScheduleProjection'"));
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Enable_works_without_a_schedule_projection()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: false);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var lifecycle = new SqlJobLifecycle(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System);
        var receipt = await lifecycle.ApplyAsync(jobId, "Enable", await VersionAsync(database, jobId), Actor, default);
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        var scheduleId = await ScheduleIdAsync(database, jobId);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ZoneId=N'Etc/UTC',CursorUtc=DATEADD(minute,-5,SYSUTCDATETIME()),ActivatedUtc=DATEADD(minute,-5,SYSUTCDATETIME()) WHERE ScheduleId='{scheduleId}'");
        var sweep = new SqlScheduleSweep(options, new OccurrenceWriter(options, new FiveFieldScheduleEvaluator()));
        Assert.True(await sweep.RunDueAsync(DateTimeOffset.UtcNow.AddMinutes(3), 10, default) > 0);

        Assert.Equal(receipt, await commands.GetCommandAsync(receipt.CommandId, default));
        Assert.Equal(1, await database.ScalarAsync($"SELECT Enabled FROM [workerdock].Jobs WHERE JobId='{jobId}'"));
        Assert.True(await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND TriggerKind=N'Scheduled'") > 0);
        Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EventType=N'ScheduleProjection'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Saving_an_edited_paused_schedule_keeps_it_paused()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: false);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var lifecycle = new SqlJobLifecycle(options, null, new QuartzScheduleEvaluator(), TimeProvider.System);
        await lifecycle.ApplyAsync(jobId, "PauseSchedule", await VersionAsync(database, jobId), Actor, default);
        var pausedAt = await ScalarStringAsync(database, $"SELECT CONVERT(nvarchar(33),PausedAtUtc,126) FROM [workerdock].Schedules WHERE JobId='{jobId}'");
        var editor = new SqlScheduleCommands(options, new QuartzScheduleEvaluator(), TimeProvider.System);
        var current = await editor.GetAsync(jobId, default);

        await editor.SaveAsync(jobId, current.Definition with { Cron = "*/2 * * * *" }, current.Policy, current.Version, Actor, default);

        Assert.Equal(1, await database.ScalarAsync($"SELECT Paused FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
        Assert.Equal(pausedAt, await ScalarStringAsync(database, $"SELECT CONVERT(nvarchar(33),PausedAtUtc,126) FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Competing_dispatchers_materialize_each_logical_occurrence_once()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        var start = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:00:00' WHERE ScheduleId='{scheduleId}'");
        var writerA = new OccurrenceWriter(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), new QuartzScheduleEvaluator());
        var writerB = new OccurrenceWriter(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), new QuartzScheduleEvaluator());
        var through = new DateTimeOffset(start.AddMinutes(2), TimeSpan.Zero);

        var created = await Task.WhenAll(
            writerA.MaterializeAsync(scheduleId, 1, through, default).AsTask(),
            writerB.MaterializeAsync(scheduleId, 1, through, default).AsTask());

        Assert.Equal(2, created.Sum());
        Assert.Equal(2, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND TriggerKind=N'Scheduled'"));
        Assert.Equal(2, await database.ScalarAsync($"SELECT COUNT(DISTINCT ScheduledForUtc) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND TriggerKind=N'Scheduled'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Repeated_sweep_materializes_each_occurrence_once()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:00:00' WHERE ScheduleId='{scheduleId}'");
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var sweep = new SqlScheduleSweep(options, new OccurrenceWriter(options, new FiveFieldScheduleEvaluator()));
        var through = new DateTimeOffset(2026, 10, 18, 0, 2, 0, TimeSpan.Zero);

        Assert.Equal(2, await sweep.RunDueAsync(through, 10, default));
        Assert.Equal(0, await sweep.RunDueAsync(through, 10, default));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND ScheduledForUtc='2026-10-18T00:01:00'"));
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Older_occurrence_horizon_does_not_move_cursor_back_or_duplicate_work()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:00:00' WHERE ScheduleId='{scheduleId}'");
        var writer = new OccurrenceWriter(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), new QuartzScheduleEvaluator());

        Assert.Equal(2, await writer.MaterializeAsync(scheduleId, 1, new DateTimeOffset(2026, 10, 18, 0, 2, 0, TimeSpan.Zero), default));
        Assert.Equal(0, await writer.MaterializeAsync(scheduleId, 1, new DateTimeOffset(2026, 10, 18, 0, 1, 0, TimeSpan.Zero), default));

        Assert.Equal("2026-10-18T00:02:00.0000000", await ScalarStringAsync(database, $"SELECT CONVERT(nvarchar(33),CursorUtc,126) FROM [workerdock].Schedules WHERE ScheduleId='{scheduleId}'"));
        Assert.Equal(2, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND TriggerKind=N'Scheduled'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Exhausted_once_schedule_does_not_consume_the_due_scan_page()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var exhaustedJob = await SeedScheduledJobAsync(database, enabled: true);
        var activeJob = await SeedScheduledJobAsync(database, enabled: true);
        var onceId = await ScheduleIdAsync(database, exhaustedJob);
        var cronId = await ScheduleIdAsync(database, activeJob);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Kind=N'Once',Cron=NULL,OneTimeUtc='2026-10-18T00:00:00',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:01:00' WHERE ScheduleId='{onceId}'; UPDATE [workerdock].Schedules SET Kind=N'Cron',Cron=N'* * * * *',OneTimeUtc=NULL,ZoneId=N'Etc/UTC',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:01:30' WHERE ScheduleId='{cronId}'");
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var sweep = new SqlScheduleSweep(options, new OccurrenceWriter(options, new FiveFieldScheduleEvaluator()));

        var created = await sweep.RunDueAsync(new DateTimeOffset(2026, 10, 18, 0, 2, 0, TimeSpan.Zero), 1, default);

        Assert.Equal(1, created);
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{cronId}' AND ScheduledForUtc='2026-10-18T00:02:00'"));
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Stale_schedule_revision_cannot_materialize_work()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var writer = new OccurrenceWriter(options, new FiveFieldScheduleEvaluator());

        Assert.Equal(0, await writer.MaterializeAsync(scheduleId, 2, DateTimeOffset.UtcNow, default));
        Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}'"));
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Catch_up_count_is_shared_across_bounded_occurrence_pages()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:00:00',MissedMode=N'CatchUp',LatenessToleranceMs=60000,CatchUpCount=3,CatchUpAgeMs=10800000 WHERE ScheduleId='{scheduleId}'");
        var writer = new OccurrenceWriter(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), new QuartzScheduleEvaluator());
        var horizon = new DateTimeOffset(2026, 10, 18, 2, 30, 0, TimeSpan.Zero);

        var firstPage = await writer.MaterializeAsync(scheduleId, 1, horizon, default);
        var secondPage = await writer.MaterializeAsync(scheduleId, 1, horizon, default);

        Assert.Equal(3, firstPage);
        Assert.Equal(0, secondPage);
        Assert.Equal(3, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND TriggerKind=N'Scheduled'"));
        Assert.True(await database.ScalarAsync($"SELECT SUM(SkippedOccurrences) FROM [workerdock].ScheduleRecoveryEvents WHERE ScheduleId='{scheduleId}'") >= 147);
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Changed_timezone_rules_advance_schedule_revision_without_projection()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var store = new SqlScheduleSweep(options, new OccurrenceWriter(options, new FiveFieldScheduleEvaluator()), new FixedRulesFingerprint(new string('B', 64)));

        Assert.Equal(1, await store.RefreshZoneRulesAsync(100, default));

        Assert.Equal(2, await database.ScalarAsync($"SELECT Revision FROM [workerdock].Schedules WHERE ScheduleId='{scheduleId}'"));
        Assert.Equal(new string('B', 64), await ScalarStringAsync(database, $"SELECT ZoneRulesFingerprint FROM [workerdock].Schedules WHERE ScheduleId='{scheduleId}'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventType=N'ScheduleZoneRulesChanged'"));
        Assert.Equal(0, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EntityId='{scheduleId}' AND EventType=N'ScheduleProjection'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Zone_rule_refresh_rotates_beyond_a_bounded_page()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        for (var i = 0; i < 105; i++) await SeedScheduledJobAsync(database, enabled: false);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var store = new SqlScheduleSweep(options, new OccurrenceWriter(options, new FiveFieldScheduleEvaluator()), new FixedRulesFingerprint(new string('B', 64)));

        Assert.Equal(100, await store.RefreshZoneRulesAsync(100, default));
        Assert.Equal(5, await store.RefreshZoneRulesAsync(100, default));
        Assert.Equal(105, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Schedules WHERE ZoneRulesFingerprint=REPLICATE('B',64) AND Revision=2"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Manual_run_remains_available_while_schedule_is_paused()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var lifecycle = new SqlJobLifecycle(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), null, new QuartzScheduleEvaluator(), TimeProvider.System);
        await lifecycle.ApplyAsync(jobId, "PauseSchedule", await VersionAsync(database, jobId), Actor, default);
        var commands = new SqlJobCommands(new TenantDatabaseOptions(database.ConnectionString, database.InstanceId), JobCommandTestCatalog.Create());

        var receipt = await commands.RunNowAsync(jobId, "manual-during-pause", Actor, default);

        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{receipt.TargetId}' AND TriggerKind=N'Manual'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT Paused FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Schedule_save_and_delete_receipts_are_applied_without_a_projection_worker()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: false);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var commands = new SqlScheduleCommands(options, new FiveFieldScheduleEvaluator(), TimeProvider.System);
        var current = await commands.GetAsync(jobId, default);

        var saved = await commands.SaveAsync(jobId, current.Definition with { Cron = "*/2 * * * *" }, current.Policy, current.Version, Actor, default);
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Commands WHERE CommandId='{saved.CommandId}' AND State=N'Applied'"));
        var deleted = await commands.DeleteAsync(jobId, await VersionAsync(database, jobId), Actor, default);

        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Commands WHERE CommandId='{deleted.CommandId}' AND State=N'Applied'"));
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Concurrent_schedule_edits_with_one_etag_reject_the_loser()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: false);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var commands = new SqlScheduleCommands(options, new QuartzScheduleEvaluator(), TimeProvider.System);
        var etag = await VersionAsync(database, jobId);
        ScheduleDefinition Definition(string cron) => new(Guid.NewGuid(), 1, "Cron", cron, "UTC", null, DateTimeOffset.UtcNow);
        async Task<bool> TrySaveAsync(string cron)
        {
            try
            {
                await commands.SaveAsync(jobId, Definition(cron), MissedWorkPolicy.Default, etag, Actor, default);
                return true;
            }
            catch (WorkerDockException error) when (error.Code == "StaleVersion") { return false; }
        }

        var results = await Task.WhenAll(TrySaveAsync("*/2 * * * *"), TrySaveAsync("*/3 * * * *"));

        Assert.Equal(1, results.Count(value => value));
        Assert.Equal(1, results.Count(value => !value));
        Assert.Equal(2, await database.ScalarAsync($"SELECT Revision FROM [workerdock].Schedules WHERE JobId='{jobId}'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Materialized_execution_pins_the_current_profile_revision()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var profiles = new SqlConnectionProfiles(options);
        var proposed = SqlTemplateTests.Profile();
        await profiles.SaveAsync(proposed, null, Actor, default);
        var original = await profiles.GetCurrentAsync(proposed.ProfileId, default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true, proposed.ProfileId);
        await profiles.SaveAsync(proposed with { Server = "rotated" }, original!.Version, Actor, default);
        var current = await profiles.GetCurrentAsync(proposed.ProfileId, default);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:00:00' WHERE ScheduleId='{scheduleId}'");

        await new OccurrenceWriter(options, new QuartzScheduleEvaluator()).MaterializeAsync(scheduleId, 1, new DateTimeOffset(2026, 10, 18, 0, 1, 0, TimeSpan.Zero), default);

        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}' AND ProfileRevisionId='{current!.Current.ProfileRevisionId}'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Archive_tombstones_future_schedule_but_preserves_running_execution()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var runner = new WorkerDock.Infrastructure.Migrations.MigrationRunner(database.ConnectionString, database.InstanceId, Path.Combine(AppContext.BaseDirectory, "database"));
        await runner.ApplyAsync("tenant", "0009_dynamic_values", default);
        var jobId = await SeedScheduledJobAsync(database, enabled: true);
        var scheduleId = await ScheduleIdAsync(database, jobId);
        await database.ExecuteAsync($"UPDATE [workerdock].Schedules SET Cron=N'* * * * *',ActivatedUtc='2026-10-18T00:00:00',CursorUtc='2026-10-18T00:00:00' WHERE ScheduleId='{scheduleId}'");
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        var writer = new OccurrenceWriter(options, new QuartzScheduleEvaluator());
        await writer.MaterializeAsync(scheduleId, 1, new DateTimeOffset(2026, 10, 18, 0, 1, 0, TimeSpan.Zero), default);
        var executionId = Guid.Parse(await ScalarStringAsync(database, $"SELECT CONVERT(nvarchar(36),ExecutionId) FROM [workerdock].Executions WHERE ScheduleId='{scheduleId}'"));
        await database.ExecuteAsync($"UPDATE [workerdock].Executions SET State=N'Running' WHERE ExecutionId='{executionId}'");
        var lifecycle = new SqlJobLifecycle(options, null, new QuartzScheduleEvaluator(), TimeProvider.System);

        await lifecycle.ApplyAsync(jobId, "Archive", await VersionAsync(database, jobId), Actor, default);
        var materializedAfterArchive = await writer.MaterializeAsync(scheduleId, 1, new DateTimeOffset(2026, 10, 18, 0, 3, 0, TimeSpan.Zero), default);

        Assert.Equal(0, materializedAfterArchive);
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE ExecutionId='{executionId}' AND State=N'Running'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Schedules WHERE ScheduleId='{scheduleId}' AND Removed=1 AND Revision=2"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Enable_releases_an_accepted_held_delivery()
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        var options = new TenantDatabaseOptions(database.ConnectionString, database.InstanceId);
        await SqlCapacityTestSetup.ReadyAsync(options, "enable-held-worker");
        var commands = new SqlJobCommands(options, JobCommandTestCatalog.Create());
        var job = new JobRevision(Guid.NewGuid(), 0, "held retry", "workerdock.http", "1.0.0", System.Text.Json.JsonSerializer.SerializeToElement(new { }), null,
            new(null, 3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 0, ["Busy"], true, "Queue", 20, 5));
        await commands.SaveAsync(job, null, Actor, default);
        var lifecycle = new SqlJobLifecycle(options, JobCommandTestCatalog.Create(), new QuartzScheduleEvaluator(), TimeProvider.System);
        await lifecycle.ApplyAsync(job.JobId, "EnableManual", await VersionAsync(database, job.JobId), Actor, default);
        var execution = await commands.RunNowAsync(job.JobId, "held-before-disable", Actor, default);
        await database.ExecuteAsync($"UPDATE [workerdock].Outbox SET ProjectionState=N'Held' WHERE EntityId='{execution.TargetId}' AND EventType=N'ExecutionDispatch'");
        await lifecycle.ApplyAsync(job.JobId, "Disable", await VersionAsync(database, job.JobId), Actor, default);

        await lifecycle.ApplyAsync(job.JobId, "Enable", await VersionAsync(database, job.JobId), Actor, default);

        Assert.Equal(1, await database.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Outbox WHERE EntityId='{execution.TargetId}' AND EventType=N'ExecutionDispatch' AND ProjectionState=N'Pending'"));
    }

    private static async Task<Guid> SeedScheduledJobAsync(SqlDatabaseFixture database, bool enabled, Guid? profileId = null)
    {
        var jobId = Guid.NewGuid(); var scheduleId = Guid.NewGuid();
        var profile = profileId is { } id ? $"'{id}'" : "NULL";
        await database.ExecuteAsync($"INSERT [workerdock].Jobs(JobId,CurrentRevision,Enabled,Archived,ConcurrencyLimit) VALUES('{jobId}',NULL,{(enabled ? 1 : 0)},0,1)");
        await database.ExecuteAsync($"INSERT [workerdock].JobRevisions(JobId,Revision,Name,TemplateId,TemplateVersion,ConfigurationJson,ProfileId,PolicyJson,CreatedAtUtc) VALUES('{jobId}',1,N'test',N'workerdock.http',N'1.0.0',N'{{}}',{profile},N'{{\"Timeout\":null,\"MaxAttempts\":1,\"RetryBaseDelay\":\"00:00:00\",\"RetryMaxDelay\":\"00:00:00\",\"JitterRatio\":0,\"RetryableCodes\":[],\"ReplaySafe\":false,\"Overlap\":\"Queue\",\"BacklogLimit\":10,\"Priority\":5}}',SYSUTCDATETIME())");
        await database.ExecuteAsync($"UPDATE [workerdock].Jobs SET CurrentRevision=1,ActivationBoundaryUtc=SYSUTCDATETIME() WHERE JobId='{jobId}'");
        await database.ExecuteAsync($"INSERT [workerdock].Schedules(ScheduleId,JobId,Revision,Kind,Cron,ZoneId,OneTimeUtc,ActivatedUtc,Paused,Removed,PausedAtUtc,CursorUtc,MissedMode,LatenessToleranceMs,CatchUpCount,CatchUpAgeMs,ZoneRulesFingerprint) VALUES('{scheduleId}','{jobId}',1,N'Cron',N'*/5 * * * *',N'UTC',NULL,SYSUTCDATETIME(),0,0,NULL,SYSUTCDATETIME(),N'Skip',60000,0,0,REPLICATE('A',64))");
        return jobId;
    }

    private static async Task<byte[]> VersionAsync(SqlDatabaseFixture database, Guid jobId)
    {
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT Version FROM [workerdock].Jobs WHERE JobId=@id"; command.Parameters.Add(new("@id", System.Data.SqlDbType.UniqueIdentifier) { Value = jobId });
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> ScheduleIdAsync(SqlDatabaseFixture database, Guid jobId) => Guid.Parse(await ScalarStringAsync(database, $"SELECT CONVERT(nvarchar(36),ScheduleId) FROM [workerdock].Schedules WHERE JobId='{jobId}'"));

    private static async Task<string> ScalarStringAsync(SqlDatabaseFixture database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedRulesFingerprint(string value) : WorkerDock.Domain.Scheduling.IScheduleRulesFingerprint
    {
        public string GetFingerprint(string zoneId) => value;
    }
}
