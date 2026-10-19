using System.Text.Json;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.SampleTemplate;

public sealed class SampleTemplate : IWorkerTemplate
{
    private static readonly TemplateDescriptor TemplateDescriptor = CreateDescriptor();

    public TemplateDescriptor Descriptor => TemplateDescriptor;

    public IReadOnlyList<ValidationIssue> Validate(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
            return [new ValidationIssue("$", "type")];

        if (!configuration.TryGetProperty("text", out var text))
            return [new ValidationIssue("$.text", "required")];

        if (text.ValueKind != JsonValueKind.String)
            return [new ValidationIssue("$.text", "type")];

        var value = text.GetString()!;
        if (value.Length is < 1 or > 256)
            return [new ValidationIssue("$.text", value.Length == 0 ? "minLength" : "maxLength")];

        return [];
    }

    public async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var issues = Validate(context.Execution.Configuration);
        if (issues.Count != 0)
        {
            return new TemplateResult(
                "PermanentFailure",
                "TemplateConfigurationInvalid",
                false,
                JsonSerializer.SerializeToElement(new { issueCount = issues.Count }),
                null);
        }

        var value = context.Execution.Configuration.GetProperty("text").GetString()!;
        var normalizedLength = value.Trim().ToUpperInvariant().Length;
        await context.Log.WriteAsync(
            "TemplateCompleted",
            new Dictionary<string, string> { ["outcome"] = "Success" },
            ct);

        return new TemplateResult(
            "Success",
            "Success",
            false,
            JsonSerializer.SerializeToElement(new { normalizedLength }),
            null);
    }

    private static TemplateDescriptor CreateDescriptor()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["text"],
              "properties": {
                "text": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 256,
                  "title": "Text",
                  "description": "Text to normalize",
                  "default": "hello",
                  "x-workerDock-labelKey": "SampleTemplate.text",
                  "x-workerDock-descriptionKey": "SampleTemplate.textDescription"
                }
              }
            }
            """);
        using var hints = JsonDocument.Parse("""
            {
              "fields": {
                "text": { "control": "textarea" }
              }
            }
            """);

        return TemplateDescriptorMetadata.WithDigest(new TemplateDescriptor(
            "workerdock.sample-normalize",
            "1.0.0",
            "Sample text normalizer",
            "Trims and measures text without performing external side effects.",
            schema.RootElement.Clone(),
            hints.RootElement.Clone(),
            ""));
    }
}
