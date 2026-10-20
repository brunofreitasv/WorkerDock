using WorkerDock.Contracts.Templates;

namespace WorkerDock.Application.Templates;

public interface ITemplateAuthoringService
{
    IReadOnlyList<ValidationIssue> Validate(TemplateAuthoringContext context);
}
