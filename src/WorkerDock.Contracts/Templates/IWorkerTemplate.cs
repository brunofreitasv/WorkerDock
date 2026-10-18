using System.Text.Json;
namespace WorkerDock.Contracts.Templates;
public interface IWorkerTemplate
{
    TemplateDescriptor Descriptor { get; }
    IReadOnlyList<ValidationIssue> Validate(JsonElement configuration);
    ValueTask<TemplateResult> ExecuteAsync(TemplateExecutionContext context, CancellationToken ct);
}
