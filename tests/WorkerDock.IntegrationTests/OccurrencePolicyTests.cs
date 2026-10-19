using WorkerDock.Domain.Scheduling;

namespace WorkerDock.IntegrationTests;

public sealed class OccurrencePolicyTests
{
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
