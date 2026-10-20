using System.Text.Json;
using WorkerDock.Contracts.Logging;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.TestTemplates;

public sealed class ResolvedValueTemplate(string version = "1.1.0") : IWorkerTemplate
{
    public TemplateDescriptor Descriptor => new("resolved", version, "Resolved test", "Test only",
        JsonSerializer.SerializeToElement(new
        {
            type = "object", required = new[] { "path", "value" }, additionalProperties = false,
            properties = new
            {
                path = new { type = "string" },
                value = new Dictionary<string, object> { ["type"] = "string", ["x-workerDock-dynamicValue"] = true }
            }
        }), JsonSerializer.SerializeToElement(new { }), "test-resolved-v1");

    public IReadOnlyList<ValidationIssue> Validate(JsonElement configuration) => [];

    public async ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct)
    {
        var path = context.Execution.Configuration.GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(path, "entries"), "entered", ct);
        return new("Success", "Resolved", false,
            JsonSerializer.SerializeToElement(new { value = context.Execution.Configuration.GetProperty("value").GetString() }), null);
    }
}
