using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Scheduling;

namespace WorkerDock.IntegrationTests;

public sealed class CalendarExecutionTests
{
    [Theory]
    [InlineData("30 2 * * *", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T06:30:00Z")]
    [InlineData("30 1 * * *", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-01T05:30:00Z")]
    [InlineData("0 0 1 1 *", "Etc/UTC", "2026-01-01T00:00:00Z", "2027-01-01T00:00:00Z")]
    public async Task Preview_uses_the_same_dst_and_sparse_occurrence_rules_as_materialization(string expression, string zone, string after, string expected)
    {
        var evaluator = new FiveFieldScheduleEvaluator();
        var schedule = new ScheduleDefinition(Guid.NewGuid(), 1, "Cron", expression, zone, null, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var commands = new SqlScheduleCommands(new TenantDatabaseOptions("Server=unused", Guid.NewGuid()), evaluator, TimeProvider.System);

        var preview = await commands.PreviewAsync(schedule, DateTimeOffset.Parse(after), 1, default);
        var materializedOccurrence = evaluator.Next(schedule, DateTimeOffset.Parse(after), 1);

        Assert.Equal(DateTimeOffset.Parse(expected), Assert.Single(preview));
        Assert.Equal(materializedOccurrence, preview);
    }
}