using WorkerDock.Domain.Scheduling;
using WorkerDock.Infrastructure.Scheduling;

namespace WorkerDock.IntegrationTests;

public sealed class OccurrencePolicyTests
{
    [Fact]
    public void Five_field_occurrences_feed_missed_work_policy_in_utc_order()
    {
        var activation = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        var schedule = new ScheduleDefinition(Guid.NewGuid(), 1, "Cron", "30 1 * * *", "America/New_York", null, activation);
        var due = new FiveFieldScheduleEvaluator().Next(schedule, activation, 4);
        var selected = new MissedWorkPolicy("Coalesce", TimeSpan.FromSeconds(60), 0, TimeSpan.Zero)
            .SelectOccurrences(due, activation, new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new[] { "2026-10-30T05:30:00Z", "2026-10-31T05:30:00Z", "2026-11-01T05:30:00Z", "2026-11-02T06:30:00Z" },
            due.Select(x => x.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        Assert.Equal([due[3]], selected);
    }

    [Fact]
    public void Recovery_wakeup_applies_business_policy_once()
    {
        var start = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var due = Enumerable.Range(1, 8).Select(i => start.AddMinutes(i)).ToArray();
        var through = start.AddMinutes(10);

        var skipped = MissedWorkPolicy.Default.SelectOccurrences(due, start, through);
        var coalesced = new MissedWorkPolicy("Coalesce", TimeSpan.FromSeconds(60), 0, TimeSpan.Zero)
            .SelectOccurrences(due, start, through);
        var boundedCatchUp = new MissedWorkPolicy("CatchUp", TimeSpan.FromSeconds(60), 3, TimeSpan.FromMinutes(7))
            .SelectOccurrences(due, start, through);
        var repeatedWakeup = new MissedWorkPolicy("CatchUp", TimeSpan.FromSeconds(60), 3, TimeSpan.FromMinutes(7))
            .SelectOccurrences(due, start, through);

        Assert.Empty(skipped);
        Assert.Single(coalesced);
        Assert.Equal(3, boundedCatchUp.Count);
        Assert.Equal(boundedCatchUp, repeatedWakeup);
        Assert.Equal(due[2], boundedCatchUp[0]);
    }

    [Fact]
    public void Recovery_classifies_each_due_occurrence_and_bounds_catch_up_with_recent_work()
    {
        var activation = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var due = Enumerable.Range(1, 5).Select(i => activation.AddMinutes(i)).ToArray();
        var through = activation.AddMinutes(5).AddSeconds(30);

        var skip = MissedWorkPolicy.Default.SelectOccurrences(due, activation, through);
        var coalesce = new MissedWorkPolicy("Coalesce", TimeSpan.FromSeconds(60), 0, TimeSpan.Zero)
            .SelectOccurrences(due, activation, through);
        var catchUp = new MissedWorkPolicy("CatchUp", TimeSpan.FromSeconds(60), 3, TimeSpan.FromMinutes(10))
            .SelectOccurrences(due, activation, through);

        Assert.Equal([due[4]], skip);
        Assert.Equal([due[3], due[4]], coalesce);
        Assert.Equal([due[0], due[1], due[4]], catchUp);
    }

    [Fact]
    public void Catch_up_budget_is_consumed_once_across_two_bounded_pages()
    {
        var activation = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var horizon = activation.AddMinutes(150);
        var occurrences = Enumerable.Range(1, 150).Select(i => activation.AddMinutes(i)).ToArray();
        var policy = new MissedWorkPolicy("CatchUp", TimeSpan.FromSeconds(60), 3, TimeSpan.FromHours(3));
        var budget = new CatchUpRecoveryBudget(policy.CatchUpCount, 0);

        var first = policy.SelectOccurrences(occurrences.Take(100), activation, horizon, budget.Remaining);
        budget = budget.Consume(first.Count);
        var second = policy.SelectOccurrences(occurrences.Skip(100), activation, horizon, budget.Remaining);
        budget = budget.Consume(second.Count);

        Assert.Equal(3, first.Count);
        Assert.Empty(second);
        Assert.Equal(3, budget.Created);
        Assert.Equal(0, budget.Remaining);
    }

    [Theory]
    [InlineData("CatchUp", 0, 1)]
    [InlineData("CatchUp", 3, 0)]
    [InlineData("CatchUp", 101, 1)]
    [InlineData("Other", 0, 0)]
    public void Invalid_policy_is_rejected(string mode, int count, int ageMinutes)
    {
        var policy = new MissedWorkPolicy(mode, TimeSpan.FromSeconds(60), count, TimeSpan.FromMinutes(ageMinutes));
        Assert.Throws<ArgumentException>(() => policy.SelectOccurrences([], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }
}
