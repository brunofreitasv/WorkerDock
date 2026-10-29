using WorkerDock.Application.Templates;
using WorkerDock.Infrastructure.Persistence;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Templates.Http;
using WorkerDock.Templates.SqlServer;

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

    public static async ValueTask RegisterSqlAsync(TenantDatabaseOptions options)
    {
        var descriptors = new[] { HttpTemplateDescriptors.Get("1.1.0"), SqlTemplateDescriptors.Get("1.1.0") };
        await new SqlTemplateCatalog(options, descriptors).RegisterAsync(descriptors, default);
    }

    private static WorkerDock.Contracts.Templates.TemplateDescriptor Descriptor(string id) =>
        TemplateMetadata.WithDigest(TemplateCatalogTests.Descriptor("""{"type":"object"}""") with { Id = id, Version = "1.1.0" });
}
