using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Jobs;
using WorkerDock.Application.Scheduling;
using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Jobs;
using WorkerDock.Infrastructure.Scheduling;

namespace WorkerDock.IntegrationTests;

public sealed class JobDefinitionEditTests
{
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Duplicate_copies_failure_detail_policy_for_both_values()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        var queries = new SqlJobManagementQueries(options);
        var duplicator = new SqlJobLifecycle(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System);
        foreach (var hide in new[] { false, true })
        {
            var source = await JobManagementTestData.SeedAsync(db, "Source " + hide);
            await db.ExecuteAsync($"UPDATE [workerdock].JobRevisions SET HideFailureDetails={(hide ? 1 : 0)} WHERE JobId='{source}'");
            var current = await queries.GetAsync(source, default);
            var target = Guid.NewGuid();
            await duplicator.DuplicateAsync(source, target, current.Editor.Version, Actor, default);
            Assert.Equal(hide, (await queries.GetAsync(target, default)).Editor.Revision.HideFailureDetails);
        }
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task One_time_precision_offset_unknown_configuration_and_nondefault_policy_round_trip_without_schedule_reset()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "One time"); await JobManagementTestData.ScheduleAsync(db, id, paused: true);
        await db.ExecuteAsync($"UPDATE [workerdock].Schedules SET Kind=N'Once',Cron=NULL,ZoneId=N'America/Sao_Paulo',OneTimeUtc='2026-11-02T03:34:56.1234567' WHERE JobId='{id}'");
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        var queries = new SqlJobManagementQueries(options); var before = await queries.GetAsync(id, default);
        Assert.Equal(TimeSpan.FromHours(-3), before.Schedule!.SelectedOffset);
        Assert.Equal(1234567, before.Schedule.LocalOnce!.Value.Ticks % TimeSpan.TicksPerSecond);
        var revision = before.Editor.Revision with
        {
            Configuration = JsonSerializer.SerializeToElement(new { unknownSupportedField = new { nested = "preserved", count = 17 } }),
            Policy = new(TimeSpan.FromSeconds(45), 7, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2), 0.25, ["Busy", "Timeout"], true, "Allow", 27, 9),
            HideFailureDetails = true
        };
        var commands = new SqlJobDefinitionCommands(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System);
        await commands.SaveDefinitionAsync(id, revision, before.Schedule, before.MissedPolicy, before.Editor.Version, Actor, default);
        var after = await queries.GetAsync(id, default);
        Assert.Equal(before.Schedule, after.Schedule); Assert.Equal(before.MissedPolicy, after.MissedPolicy); Assert.True(after.SchedulePaused);
        Assert.Equal(revision.Configuration.GetRawText(), after.Editor.Revision.Configuration.GetRawText());
        Assert.Equal(JsonSerializer.Serialize(revision.Policy), JsonSerializer.Serialize(after.Editor.Revision.Policy));
        Assert.False(before.Editor.Revision.HideFailureDetails);
        Assert.True(after.Editor.Revision.HideFailureDetails);
        Assert.Equal(1, await db.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].JobRevisions WHERE JobId='{id}' AND Revision=2 AND HideFailureDetails=1"));
        Assert.Equal(1, await db.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Schedules WHERE JobId='{id}' AND Revision=1 AND CursorUtc='2020-01-01'"));
    }
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Enabled_edit_validates_proposed_configuration_before_publishing_revision_or_schedule()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Enabled", enabled: true); await JobManagementTestData.ScheduleAsync(db, id);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        var before = await new SqlJobManagementQueries(options).GetAsync(id, default);
        var descriptor = WorkerDock.Infrastructure.Templates.TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object","properties":{"requiredValue":{"type":"integer"}},"required":["requiredValue"]}""") with { Id = "workerdock.http", Version = "1.1.0" });
        var catalog = new WorkerDock.Infrastructure.Templates.TemplateCatalog([descriptor]); await catalog.RegisterAsync([descriptor], default);
        var commands = new SqlJobDefinitionCommands(options, catalog, new FiveFieldScheduleEvaluator(), TimeProvider.System);
        Assert.Equal("ValidationFailed", (await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.SaveDefinitionAsync(id, before.Editor.Revision with { Name = "Invalid enabled edit" }, before.Schedule! with { Cron = "*/2 * * * *" }, before.MissedPolicy, before.Editor.Version, Actor, default))).Code);
        var after = await new SqlJobManagementQueries(options).GetAsync(id, default);
        Assert.Equal(before.Editor.Version, after.Editor.Version); Assert.Equal(before.Schedule, after.Schedule); Assert.True(after.Editor.Enabled);
        Assert.Equal(1, after.Editor.Revision.Revision);
    }
    [Theory]
    [InlineData("America/New_York", "2026-03-08T02:30:00", null, "localOnce")]
    [InlineData("America/New_York", "2026-11-01T01:30:00", null, "localOnce")]
    [InlineData("UTC", "2026-11-02T01:30:00", "01:00:00", "selectedOffset")]
    [InlineData("Missing/Zone", "2026-11-02T01:30:00", null, "zoneId")]
    public void Once_conversion_returns_field_validation_for_invalid_local_time_or_zone(string zone, string local, string? offset, string field)
    {
        var input = new ScheduleEditorInput("Once", null, zone, DateTime.SpecifyKind(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified), offset is null ? null : TimeSpan.Parse(offset, System.Globalization.CultureInfo.InvariantCulture));
        var error = Assert.Throws<WorkerDockException>(() => ScheduleInputConverter.ConvertInput(input));
        Assert.Equal("ValidationFailed", error.Code); Assert.Equal(field, error.Field);
    }
    private static readonly ActorContext Actor = new("manager", "Entra", Guid.NewGuid());
    [Trait("Store", "Sql")]
    [Fact]
    public async Task Name_only_edit_preserves_enabled_pause_cursor_complete_policy_and_pinned_execution()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Before", enabled: true);
        await JobManagementTestData.ScheduleAsync(db, id, paused: true);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        var queries = new SqlJobManagementQueries(options);
        var before = await queries.GetAsync(id, default);
        var commands = new SqlJobDefinitionCommands(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System);
        await db.ExecuteAsync($"INSERT [workerdock].Executions(ExecutionId,JobId,JobRevision,TriggerKind,IdempotencyKey,State,Certainty,AvailableAtUtc,CorrelationId,CreatedAtUtc) VALUES(NEWID(),'{id}',1,N'Manual',CONVERT(nvarchar(36),NEWID()),N'Queued',N'Known',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME())");
        await commands.SaveDefinitionAsync(id, before.Editor.Revision with { Name = "After" }, before.Schedule, before.MissedPolicy, before.Editor.Version, Actor, default);
        var after = await queries.GetAsync(id, default);
        Assert.True(after.Editor.Enabled);
        Assert.True(after.SchedulePaused);
        Assert.Equal(before.Schedule, after.Schedule);
        Assert.Equal(before.MissedPolicy, after.MissedPolicy);
        Assert.Equal(2, after.Editor.Revision.Revision);
        Assert.Equal(1, await db.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Schedules WHERE JobId='{id}' AND Revision=1 AND CursorUtc='2020-01-01'"));
        Assert.Equal(1, await db.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].Executions WHERE JobId='{id}' AND JobRevision=1"));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM [workerdock].AuditEvents WHERE EventType=N'JobDefinitionSaved'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Removing_schedule_is_atomic_and_stale_or_invalid_edits_do_not_publish_revisions()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Before"); await JobManagementTestData.ScheduleAsync(db, id);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        var queries = new SqlJobManagementQueries(options); var before = await queries.GetAsync(id, default);
        var commands = new SqlJobDefinitionCommands(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System);
        Assert.Equal("ValidationFailed", (await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.SaveDefinitionAsync(id, before.Editor.Revision with { Name = "Rejected" }, new("Cron", "invalid", "UTC", null, null), before.MissedPolicy, before.Editor.Version, Actor, default))).Code);
        Assert.Equal("Before", (await queries.GetAsync(id, default)).Editor.Revision.Name);
        await commands.SaveDefinitionAsync(id, before.Editor.Revision with { Name = "Manual" }, null, null, before.Editor.Version, Actor, default);
        Assert.Equal("StaleVersion", (await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.SaveDefinitionAsync(id, before.Editor.Revision with { Name = "Stale" }, before.Schedule, before.MissedPolicy, before.Editor.Version, Actor, default))).Code);
        var after = await queries.GetAsync(id, default);
        Assert.Null(after.Schedule); Assert.False(after.Editor.Enabled); Assert.Equal("Manual", after.Editor.Revision.Name);
        Assert.Equal(2, await db.ScalarAsync($"SELECT COUNT(*) FROM [workerdock].JobRevisions WHERE JobId='{id}'"));
    }

    [Trait("Store", "Sql")]
    [Fact]
    public async Task Archived_definition_is_rejected_and_write_failure_rolls_back_configuration_and_schedule()
    {
        await using var db = await JobManagementTestData.CreateAsync();
        var id = await JobManagementTestData.SeedAsync(db, "Before"); await JobManagementTestData.ScheduleAsync(db, id);
        var options = new WorkerDock.Infrastructure.Persistence.TenantDatabaseOptions(db.ConnectionString, db.InstanceId);
        var queries = new SqlJobManagementQueries(options); var before = await queries.GetAsync(id, default);
        var commands = new SqlJobDefinitionCommands(options, JobCommandTestCatalog.Create(), new FiveFieldScheduleEvaluator(), TimeProvider.System);
        await db.ExecuteAsync("CREATE TRIGGER [workerdock].TestDefinitionFailure ON [workerdock].Commands AFTER INSERT AS THROW 51000, 'Synthetic rollback', 1");
        Assert.Equal("PersistenceUnavailable", (await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.SaveDefinitionAsync(id, before.Editor.Revision with { Name = "Rollback" }, before.Schedule! with { Cron = "*/2 * * * *" }, before.MissedPolicy, before.Editor.Version, Actor, default))).Code);
        var after = await queries.GetAsync(id, default);
        Assert.Equal(before.Editor.Version, after.Editor.Version); Assert.Equal(before.Schedule, after.Schedule); Assert.Equal("Before", after.Editor.Revision.Name);
        await db.ExecuteAsync($"UPDATE [workerdock].Jobs SET Archived=1 WHERE JobId='{id}'");
        Assert.Equal("JobArchived", (await Assert.ThrowsAsync<WorkerDockException>(async () => await commands.SaveDefinitionAsync(id, before.Editor.Revision, before.Schedule, before.MissedPolicy, await JobManagementTestData.VersionAsync(db, id), Actor, default))).Code);
    }
}
