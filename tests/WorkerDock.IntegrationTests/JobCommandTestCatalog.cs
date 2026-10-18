using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.IntegrationTests;

internal static class JobCommandTestCatalog
{
    public static ITemplateCatalog Create()
    {
        var descriptors = new[] { Descriptor("workerdock.http"), Descriptor("workerdock.sql-server") };
        var catalog = new TemplateCatalog(descriptors);
        catalog.RegisterAsync(descriptors, default).GetAwaiter().GetResult();
        return catalog;
    }

    private static WorkerDock.Contracts.Templates.TemplateDescriptor Descriptor(string id) =>
        TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object"}""") with { Id = id });
}
