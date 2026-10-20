using WorkerDock.Contracts.Templates;

namespace WorkerDock.Templates.SqlServer;

public static class SqlTemplateDescriptors
{
    public static TemplateDescriptor Get(string version) => version switch
    {
        "1.0.0" or "1.1.0" => TemplateDescriptorMetadata.WithDigest(SqlTemplate.CreateDescriptor(version)),
        _ => throw new ArgumentOutOfRangeException(nameof(version), "Unsupported SQL template version.")
    };
}
