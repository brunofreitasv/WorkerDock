using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
using SampleTemplateHandler = WorkerDock.SampleTemplate.SampleTemplate;

namespace WorkerDock.UnitTests;

public sealed class SampleTemplateConformanceTests
{
    [Fact]
    public void Metadata_and_validation_follow_public_contract()
    {
        var template = new SampleTemplateHandler();
        using var invalid = JsonDocument.Parse("""{"text": ""}""");
        using var valid = JsonDocument.Parse("""{"text":" hello "}""");

        Assert.Equal("1.0.0", template.Descriptor.Version);
        Assert.Equal("workerdock.sample-normalize", template.Descriptor.Id);
        Assert.Equal(
            TemplateDescriptorMetadata.ComputeDigest(template.Descriptor),
            template.Descriptor.MetadataDigest);
        Assert.Empty(template.Validate(valid.RootElement));
        Assert.Equal("minLength", Assert.Single(template.Validate(invalid.RootElement)).Code);
    }

    [Fact]
    public async Task Execution_honors_cancellation_and_emits_only_safe_summary()
    {
        const string sentinel = "secret-sentinel-value";
        var template = new SampleTemplateHandler();
        using var configuration = JsonDocument.Parse(JsonSerializer.Serialize(new { text = sentinel }));
        var execution = new ExecutionDescriptor(
            Guid.NewGuid(), 1, template.Descriptor.Id, template.Descriptor.Version,
            Guid.NewGuid().ToString("D"), configuration.RootElement.Clone(), null, null,
            JsonSerializer.SerializeToElement(new { }), null);
        var log = new RecordingLog();
        var context = new TemplateExecutionContext(execution, new RejectingVault(), log);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await template.ExecuteAsync(context, cancelled.Token));
        Assert.Empty(log.Events);

        var result = await template.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("Success", result.Outcome);
        Assert.False(result.EffectsMayHaveOccurred);
        Assert.DoesNotContain(sentinel, result.SafeSummary.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, string.Join(" ", log.Events.SelectMany(x => x.Values)), StringComparison.Ordinal);
        Assert.Contains("outcome", log.Events.Single().Keys);
    }

    private sealed class RecordingLog : ISafeExecutionLog
    {
        public List<IReadOnlyDictionary<string, string>> Events { get; } = [];

        public ValueTask WriteAsync(string eventCode, IReadOnlyDictionary<string, string> safeFields, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("TemplateCompleted", eventCode);
            Events.Add(new Dictionary<string, string>(safeFields));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RejectingVault : ISecretVault
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct) =>
            ValueTask.FromException<SecretValue>(new InvalidOperationException("The sample must not resolve secrets."));
    }
}
