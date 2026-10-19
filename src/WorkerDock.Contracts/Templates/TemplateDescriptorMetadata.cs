using System.Security.Cryptography;
using System.Text.Json;

namespace WorkerDock.Contracts.Templates;

public static class TemplateDescriptorMetadata
{
    public static TemplateDescriptor WithDigest(TemplateDescriptor descriptor) =>
        descriptor with { MetadataDigest = ComputeDigest(descriptor) };

    public static string ComputeDigest(TemplateDescriptor descriptor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(descriptor.Id);
            writer.WriteStringValue(descriptor.Version);
            writer.WriteStringValue(descriptor.DisplayName);
            writer.WriteStringValue(descriptor.Description);
            WriteCanonical(writer, descriptor.ConfigurationSchema);
            WriteCanonical(writer, descriptor.UiHints);
            writer.WriteEndArray();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var value in element.EnumerateArray()) WriteCanonical(writer, value);
            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
