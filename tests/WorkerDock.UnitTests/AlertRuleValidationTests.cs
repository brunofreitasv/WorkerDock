using System.Text.Json;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Common;
using WorkerDock.Domain.Alerts;

namespace WorkerDock.UnitTests;

public sealed class AlertRuleValidationTests
{
    [Theory]
    [InlineData("RepeatedFailure")]
    [InlineData("RetryExhausted")]
    [InlineData("Timeout")]
    [InlineData("LongRunning")]
    [InlineData("MissedExecution")]
    [InlineData("WorkerUnavailable")]
    public void Accepts_all_supported_conditions(string condition)
    {
        var rule = ValidRule(condition);
        AlertRuleValidation.Validate(rule);
    }

    [Fact]
    public void Long_running_uses_one_duration_threshold()
    {
        var error = Assert.Throws<ArgumentException>(() => AlertRuleValidation.Validate(ValidRule("LongRunning") with { Threshold = 2 }));
        Assert.Contains("Alert rule is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Alert_rule_durations_match_the_integer_seconds_storage_contract()
    {
        var error = Assert.Throws<ArgumentException>(() => AlertRuleValidation.Validate(ValidRule("Timeout") with { Window = TimeSpan.FromMilliseconds(1500) }));
        Assert.Contains("Alert rule is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Webhook_secret_headers_store_references_and_reject_inline_credentials()
    {
        var safe = Sink("""{"url":"https://hooks.example.test/alerts","headers":[{"name":"Authorization","secretReference":"alerts.receiver-token"}]}""");
        var frozen = AlertSinkValidation.ValidateAndFreeze(safe);
        Assert.Contains("alerts.receiver-token", frozen.Configuration.GetRawText(), StringComparison.Ordinal);

        var inline = Sink("""{"url":"https://hooks.example.test/alerts","headers":[{"name":"Authorization","value":"secret-sentinel"}]}""");
        var error = Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(inline));
        Assert.Equal("ValidationFailed", error.Code);
        Assert.DoesNotContain("secret-sentinel", error.Message, StringComparison.Ordinal);

        var customCredential = Sink("""{"url":"https://hooks.example.test/alerts","headers":[{"name":"X-Webhook-Token","value":"custom-secret-sentinel"}]}""");
        var customError = Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(customCredential));
        Assert.Equal("ValidationFailed", customError.Code);
        Assert.DoesNotContain("custom-secret-sentinel", customError.Message, StringComparison.Ordinal);

        var safeCustom = Sink("""{"url":"https://hooks.example.test/alerts","headers":[{"name":"X-Webhook-Token","secretReference":"alerts.receiver-token"}]}""");
        Assert.Equal("alerts.receiver-token", AlertSinkValidation.ValidateAndFreeze(safeCustom).Configuration.GetProperty("headers")[0].GetProperty("secretReference").GetString());
    }

    [Fact]
    public void Webhook_configuration_rejects_user_info_fragments_and_header_injection()
    {
        Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(Sink("""{"url":"https://user:pass@hooks.example.test/alerts"}""")));
        Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(Sink("""{"url":"https://hooks.example.test/alerts#fragment"}""")));
        Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(Sink("""{"url":"https://hooks.example.test/alerts","headers":[{"name":"X-Test","value":"ok\r\nInjected: yes"}]}""")));
        Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(Sink("""{"url":"https://hooks.example.test/alerts","headers":[{"name":"Host","secretReference":"alerts.host"}]}""")));
    }

    [Fact]
    public void Webhook_retry_and_timeout_values_match_integer_seconds_storage()
    {
        var error = Assert.Throws<WorkerDockException>(() => AlertSinkValidation.ValidateAndFreeze(
            Sink("""{"url":"https://hooks.example.test/alerts"}""") with { Timeout = TimeSpan.FromMilliseconds(1500) }));
        Assert.Equal("ValidationFailed", error.Code);
    }

    private static AlertRule ValidRule(string condition) => new(Guid.NewGuid(), "Operations", condition,
        condition is "LongRunning" or "WorkerUnavailable" ? 1 : 3,
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), Guid.NewGuid(), true);

    private static AlertSinkDefinition Sink(string json) => new(Guid.NewGuid(), "Operations webhook", "webhook", JsonDocument.Parse(json).RootElement.Clone(),
        5, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), true);
}
