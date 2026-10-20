using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Infrastructure.Templates;

public static class BuiltInAuthoringValidators
{
    public static ITemplateAuthoringService Create() => new TemplateAuthoringService(Array.Empty<ITemplateAuthoringValidator>());
}
