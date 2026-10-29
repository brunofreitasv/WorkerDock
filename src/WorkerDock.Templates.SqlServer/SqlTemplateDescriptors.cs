using WorkerDock.Contracts.Templates;

namespace WorkerDock.Templates.SqlServer;

public static class SqlTemplateDescriptors
{
    public static TemplateDescriptor Get(string version) => version == "1.1.0"
        ? TemplateDescriptorMetadata.WithDigest(SqlTemplate.CreateDescriptor())
        : throw new ArgumentOutOfRangeException(nameof(version), "Unsupported SQL template version.");
}
