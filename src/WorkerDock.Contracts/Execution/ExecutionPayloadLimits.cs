using System.Text.Json;
namespace WorkerDock.Contracts.Execution;

/// <summary>Wire budget includes the execute envelope and space for pinned nonsecret settings/profile data.</summary>
public static class ExecutionPayloadLimits
{
    public const int MaximumMessageBytes = 64 * 1024;
    public const int EnvelopeReserveBytes = 1024;
    public const int PinnedSettingsReserveBytes = 8 * 1024;
    private static readonly JsonSerializerOptions DescriptorOptions = new() { MaxDepth = 30 };
    public static bool FitsJobConfiguration(string templateId, string version, JsonElement configuration)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        var descriptor = new ExecutionDescriptor(Guid.Empty, int.MaxValue, templateId, version, new string('x', 100),
            configuration, Guid.Empty, empty, empty, DateTimeOffset.MaxValue);
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(descriptor, DescriptorOptions).Length <= MaximumMessageBytes - EnvelopeReserveBytes - PinnedSettingsReserveBytes;
        }
        catch (JsonException) { return false; }
    }
}
