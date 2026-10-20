using System.Text.Json;
using WorkerDock.Contracts.Templates;

namespace WorkerDock.Templates.Http;

public static class HttpTemplateDescriptors
{
    private static readonly TemplateDescriptor Legacy = Create("1.0.0", dynamicValues: false);
    private static readonly TemplateDescriptor Dynamic = Create("1.1.0", dynamicValues: true);

    public static TemplateDescriptor Get(string version) => version switch
    {
        "1.0.0" => Legacy,
        "1.1.0" => Dynamic,
        _ => throw new ArgumentOutOfRangeException(nameof(version), "Unsupported HTTP template version.")
    };

    private static TemplateDescriptor Create(string version, bool dynamicValues)
    {
        var dynamicAnnotation = dynamicValues ? ",\"x-workerDock-dynamicValue\":true" : "";
        var dynamicIntegerType = dynamicValues ? ",\"integer\"" : "";
        const string schemaText = """
        {"type":"object","additionalProperties":false,"required":["method","url","query","headers","secretHeaders","parameters","successStatuses","maxResponseBytes","responseMode"],"properties":{
          "method":{"type":"string","enum":["GET","POST","PUT","PATCH","DELETE","HEAD"]},"url":{"type":"string","minLength":1,"maxLength":2048},"query":{"type":"object","maxProperties":128,"additionalProperties":{"type":"string","maxLength":8192}},
          "headers":{"type":"object","maxProperties":64,"additionalProperties":{"type":"string","maxLength":8192}},"secretHeaders":{"type":"object","maxProperties":32,"additionalProperties":{"type":"object","additionalProperties":false,"required":["name"],"properties":{"name":{"type":"string","minLength":1,"maxLength":128,"x-workerDock-secretReference":true}}}},
          "parameters":{"type":"object","maxProperties":128,"additionalProperties":{"type":["string","number","boolean","null"DYNAMIC_INTEGER_TYPE]DYNAMIC_ANNOTATION}},"contentType":{"type":["string","null"],"maxLength":256},"body":{"type":["string","null"],"maxLength":65536},
          "timeoutSeconds":{"type":["integer","null"],"minimum":0,"maximum":31536000},"successStatuses":{"type":"array","default":[],"maxItems":100,"items":{"type":"integer","minimum":100,"maximum":599}},"idempotencyHeader":{"type":["string","null"],"maxLength":128},
          "maxResponseBytes":{"type":"integer","minimum":1,"maximum":2097152},"responseMode":{"type":"string","default":"SafeSummary","enum":["Discard","SafeSummary"]}}}
        """;
        var expandedSchema = schemaText.Replace("DYNAMIC_INTEGER_TYPE", dynamicIntegerType, StringComparison.Ordinal)
            .Replace("DYNAMIC_ANNOTATION", dynamicAnnotation, StringComparison.Ordinal);
        using var schema = JsonDocument.Parse(expandedSchema);
        return TemplateDescriptorMetadata.WithDigest(new TemplateDescriptor("workerdock.http", version, "HTTP request", "Send a bounded HTTP request through the deployment outbound policy.", schema.RootElement.Clone(), JsonSerializer.SerializeToElement(new { }), ""));
    }
}
