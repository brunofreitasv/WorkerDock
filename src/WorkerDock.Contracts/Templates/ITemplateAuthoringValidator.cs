using System.Text.Json;
using WorkerDock.Contracts.Values;

namespace WorkerDock.Contracts.Templates;

public sealed record TemplateAuthoringContext(TemplateDescriptor Descriptor, JsonElement Configuration,
    DynamicValuePlan? DynamicValues, TimeSpan? JobTimeout, string? TriggerKind);

public interface ITemplateAuthoringValidator
{
    string TemplateId { get; }
    string TemplateVersion { get; }
    IReadOnlyList<ValidationIssue> ValidateAuthoring(TemplateAuthoringContext context);
}
