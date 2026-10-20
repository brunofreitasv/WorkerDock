using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.Infrastructure.Templates;

public sealed class TemplateAuthoringService(IEnumerable<ITemplateAuthoringValidator> validators) : ITemplateAuthoringService
{
    private readonly ITemplateAuthoringValidator[] _validators = validators.ToArray();

    public IReadOnlyList<ValidationIssue> Validate(TemplateAuthoringContext context)
    {
        var issues = context.DynamicValues is { } plan
            ? DynamicValueValidation.Validate(context.Descriptor, context.Configuration, plan, context.TriggerKind).ToList()
            : TemplateSchemaValidator.Validate(context.Descriptor, context.Configuration).ToList();
        foreach (var validator in _validators.Where(v => v.TemplateId == context.Descriptor.Id && v.TemplateVersion == context.Descriptor.Version))
        {
            if (issues.Count >= 64) break;
            issues.AddRange(validator.ValidateAuthoring(context).Take(64 - issues.Count));
        }
        return issues.Take(64).ToArray();
    }
}
