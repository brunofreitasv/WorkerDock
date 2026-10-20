using System.Text.Json;
using System.Text;
using WorkerDock.Contracts.Values;
namespace WorkerDock.Contracts.Execution;

/// <summary>Wire budget includes the execute envelope and space for pinned nonsecret settings/profile data.</summary>
public static class ExecutionPayloadLimits
{
    public const int MaximumMessageBytes = 64 * 1024;
    public const int EnvelopeReserveBytes = 1024;
    public const int PinnedSettingsReserveBytes = 8 * 1024;
    private static readonly JsonSerializerOptions DescriptorOptions = new() { MaxDepth = 30 };
    public static bool FitsPreparedDescriptor(ExecutionDescriptor descriptor)
    {
        try
        {
            if (descriptor.ValueSnapshot is { } snapshot && JsonSerializer.SerializeToUtf8Bytes(snapshot, DescriptorOptions).Length > 16_384)
                return false;
            var envelope = new { version = 2, attemptId = Guid.Empty, generation = long.MaxValue,
                metadataDigest = new string('F', 64), execution = descriptor };
            return JsonSerializer.SerializeToUtf8Bytes(envelope, DescriptorOptions).Length + EnvelopeReserveBytes + PinnedSettingsReserveBytes <= MaximumMessageBytes;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return false; }
    }
    public static bool FitsJobConfiguration(string templateId, string version, JsonElement configuration)
        => FitsJobConfiguration(templateId, version, configuration, null);

    public static bool FitsJobConfiguration(string templateId, string version, JsonElement configuration, JsonElement? dynamicValues)
    {
        if (dynamicValues is null)
        {
            var legacy = CreateDescriptor(configuration, templateId, version);
            try { return JsonSerializer.SerializeToUtf8Bytes(legacy, DescriptorOptions).Length <= MaximumMessageBytes - EnvelopeReserveBytes - PinnedSettingsReserveBytes; }
            catch (JsonException) { return false; }
        }
        try
        {
            var plan = DynamicValueJson.Read(dynamicValues.Value);
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var binding in plan.Bindings)
            {
                var maximum = binding.Source switch
                {
                    DynamicValueSource.ReferenceDate => 10,
                    DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart => binding.Format switch
                    {
                        DynamicValueFormat.IsoOffsetTimestamp => 33,
                        DynamicValueFormat.IsoUtcTimestamp => 28,
                        DynamicValueFormat.UnixSeconds or DynamicValueFormat.UnixMilliseconds => 20,
                        _ => 33
                    },
                    DynamicValueSource.ExecutionId => 32,
                    DynamicValueSource.IdempotencyKey => 100,
                    _ => 0
                };
                snapshot[binding.Target] = new string('x', maximum);
            }
            var snapshotJson = JsonSerializer.SerializeToElement(new { ResolverVersion = plan.ResolverVersion, InputDigest = new string('F', 64), Values = snapshot });
            if (Encoding.UTF8.GetByteCount(snapshotJson.GetRawText()) > 16_384) return false;
            var descriptor = CreateDescriptor(configuration, templateId, version);
            var envelope = new { descriptor, dynamicValues, valueSnapshot = snapshotJson, valueContext = new { resolverVersion = int.MaxValue, timeZoneId = new string('x', 100), triggerKind = new string('x', 20), scheduledForUtc = DateTimeOffset.MaxValue, requestedAtUtc = DateTimeOffset.MaxValue, referenceDateOverride = "9999-12-31", firstStartedAtUtc = DateTimeOffset.MaxValue } };
            return JsonSerializer.SerializeToUtf8Bytes(envelope, DescriptorOptions).Length <= MaximumMessageBytes - EnvelopeReserveBytes - PinnedSettingsReserveBytes;
        }
        catch (Exception ex) when (ex is JsonException or ValueResolutionException or ArgumentException) { return false; }
    }

    private static ExecutionDescriptor CreateDescriptor(JsonElement configuration, string templateId, string version)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return new ExecutionDescriptor(Guid.Empty, int.MaxValue, templateId, version, new string('x', 100),
            configuration, Guid.Empty, empty, empty, DateTimeOffset.MaxValue);
    }
}
