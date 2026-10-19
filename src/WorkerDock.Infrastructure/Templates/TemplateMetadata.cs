using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Infrastructure.Templates;

public static class TemplateMetadata
{
    public static bool IsBootstrapPlaceholder(string id,string version,string digest,bool missingDescriptor) =>
        missingDescriptor && version=="1.0.0" && id is "workerdock.http" or "workerdock.sql-server" &&
        digest==Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(id+"|"+version)));
    public static TemplateDescriptor WithDigest(TemplateDescriptor descriptor) => TemplateDescriptorMetadata.WithDigest(descriptor);
    public static string ComputeDigest(TemplateDescriptor descriptor) => TemplateDescriptorMetadata.ComputeDigest(descriptor);
    public static string AggregateDigest(IEnumerable<TemplateDescriptor> descriptors)
    {
        var text = new StringBuilder();
        foreach (var d in descriptors.OrderBy(x => x.Id,StringComparer.Ordinal).ThenBy(x => x.Version,StringComparer.Ordinal))
            text.Append(d.Id).Append('|').Append(d.Version).Append('|').Append(d.MetadataDigest).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    internal static TemplateDescriptor Copy(TemplateDescriptor d) => d with { ConfigurationSchema=d.ConfigurationSchema.Clone(),UiHints=d.UiHints.Clone() };
}
