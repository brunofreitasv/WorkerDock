using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Infrastructure.Templates;

public static class BuiltInAuthoringValidators
{
    public static ITemplateAuthoringService Create() => new TemplateAuthoringService(new ITemplateAuthoringValidator[]
    {
        new WorkerDock.Templates.SqlServer.SqlConfigurationValidator("1.0.0"),
        new WorkerDock.Templates.SqlServer.SqlConfigurationValidator("1.1.0")
    });
}
