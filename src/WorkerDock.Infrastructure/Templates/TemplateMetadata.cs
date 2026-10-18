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
    public static TemplateDescriptor WithDigest(TemplateDescriptor descriptor) => descriptor with { MetadataDigest = ComputeDigest(descriptor) };
    public static string ComputeDigest(TemplateDescriptor descriptor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray(); writer.WriteStringValue(descriptor.Id); writer.WriteStringValue(descriptor.Version);
            writer.WriteStringValue(descriptor.DisplayName); writer.WriteStringValue(descriptor.Description);
            WriteCanonical(writer,descriptor.ConfigurationSchema); WriteCanonical(writer,descriptor.UiHints); writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static string AggregateDigest(IEnumerable<TemplateDescriptor> descriptors)
    {
        var text = new StringBuilder();
        foreach (var d in descriptors.OrderBy(x => x.Id,StringComparer.Ordinal).ThenBy(x => x.Version,StringComparer.Ordinal))
            text.Append(d.Id).Append('|').Append(d.Version).Append('|').Append(d.MetadataDigest).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    internal static TemplateDescriptor Copy(TemplateDescriptor d) => d with { ConfigurationSchema=d.ConfigurationSchema.Clone(),UiHints=d.UiHints.Clone() };
    internal static void WriteCanonical(Utf8JsonWriter writer,JsonElement element)
    {
        switch(element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach(var p in element.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); WriteCanonical(writer,p.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach(var v in element.EnumerateArray()) WriteCanonical(writer,v); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
}
