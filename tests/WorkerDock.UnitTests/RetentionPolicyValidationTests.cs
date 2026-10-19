using WorkerDock.Application.Retention;

namespace WorkerDock.UnitTests;

public sealed class RetentionPolicyValidationTests
{
    [Fact]
    public void Defaults_match_the_supported_history_windows()
    {
        Assert.Equal(new RetentionPolicy(30, 90, 365, false, 0), RetentionPolicy.Default);
    }

    [Theory]
    [InlineData(0, 90, 365)]
    [InlineData(30, 0, 365)]
    [InlineData(30, 90, 0)]
    [InlineData(30, 90, 3651)]
    public void Rejects_nonpositive_or_unbounded_history_days(int logDays, int executionDays, int auditDays)
    {
        var error = Assert.Throws<ArgumentException>(() => RetentionPolicyValidation.Validate(
            new RetentionPolicy(logDays, executionDays, auditDays, false, 0)));
        Assert.Contains("Retention policy is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_days_are_required_only_when_archiving_is_enabled()
    {
        RetentionPolicyValidation.Validate(new RetentionPolicy(30, 90, 365, false, 0));
        RetentionPolicyValidation.Validate(new RetentionPolicy(30, 90, 365, false, 730));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidation.Validate(new RetentionPolicy(30, 90, 365, true, 0)));
        RetentionPolicyValidation.Validate(new RetentionPolicy(30, 90, 365, true, 730));
        Assert.Throws<ArgumentException>(() => RetentionPolicyValidation.Validate(new RetentionPolicy(30, 90, 365, false, 3651)));
    }
}
