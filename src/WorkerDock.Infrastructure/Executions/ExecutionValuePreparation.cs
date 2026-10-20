using System.Security.Cryptography;
using System.Text.Json;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.Infrastructure.Executions;

public sealed record ValuePreparationResult(JsonElement? Configuration, DynamicValueSnapshot? Snapshot, string? ErrorCode);

public sealed class ExecutionValuePreparation(DynamicValueResolver resolver)
{
    public ValuePreparationResult Prepare(ExecutionDescriptor descriptor, DynamicValuePlan plan)
    {
        try
        {
            var digest = ComputeInputDigest(descriptor, plan);
            var snapshot = resolver.Resolve(plan, descriptor.ValueContext ?? throw new ValueResolutionException("DynamicSourceUnavailable", ""),
                descriptor.ExecutionId, descriptor.IdempotencyKey, digest);
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 16_384)
                throw new ValueResolutionException("DynamicSnapshotTooLarge", "");
            var configuration = Reapply(descriptor.Configuration, snapshot);
            var prepared = descriptor with { Configuration = configuration, ValueSnapshot = snapshot };
            if (!ExecutionPayloadLimits.FitsPreparedDescriptor(prepared))
                throw new ValueResolutionException("DynamicSnapshotTooLarge", "");
            return new(configuration, snapshot, null);
        }
        catch (ValueResolutionException error) { return new(null, null, error.Code); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { return new(null, null, "DynamicSnapshotInvalid"); }
    }

    public string ComputeInputDigest(ExecutionDescriptor originalDescriptor, DynamicValuePlan plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("configuration");
            WriteCanonical(writer, originalDescriptor.Configuration);
            writer.WritePropertyName("plan");
            WriteCanonical(writer, DynamicValueJson.Write(plan));
            writer.WritePropertyName("context");
            WriteCanonical(writer, JsonSerializer.SerializeToElement(originalDescriptor.ValueContext));
            writer.WriteString("executionId", originalDescriptor.ExecutionId.ToString("N"));
            writer.WriteString("idempotencyKey", originalDescriptor.IdempotencyKey);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public void VerifySnapshot(ExecutionDescriptor originalDescriptor, DynamicValuePlan plan, DynamicValueSnapshot saved)
    {
        try
        {
            var digest = ComputeInputDigest(originalDescriptor, plan);
            if (!string.Equals(saved.InputDigest, digest, StringComparison.Ordinal) ||
                saved.Values is null || saved.Values.Count != plan.Bindings.Count)
                throw new ValueResolutionException("DynamicSnapshotInvalid", "");
            var expected = resolver.Resolve(plan,
                originalDescriptor.ValueContext ?? throw new ValueResolutionException("DynamicSnapshotInvalid", ""),
                originalDescriptor.ExecutionId, originalDescriptor.IdempotencyKey, digest);
            if (saved.ResolverVersion != expected.ResolverVersion ||
                expected.Values.Any(entry => !saved.Values.TryGetValue(entry.Key, out var actual) ||
                    !string.Equals(entry.Value.GetRawText(), actual.GetRawText(), StringComparison.Ordinal)))
                throw new ValueResolutionException("DynamicSnapshotInvalid", "");
        }
        catch (Exception error) when (error is ValueResolutionException or JsonException or ArgumentException or InvalidOperationException)
        { throw new ValueResolutionException("DynamicSnapshotInvalid", ""); }
    }

    public JsonElement Reapply(JsonElement originalConfiguration, DynamicValueSnapshot snapshot)
    {
        if (snapshot.ResolverVersion != 1 || snapshot.InputDigest is null || snapshot.Values is null || snapshot.InputDigest.Length != 64 ||
            snapshot.InputDigest.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 16_384)
            throw new ValueResolutionException("DynamicSnapshotInvalid", "");
        try { return DynamicValueTargets.Apply(originalConfiguration, snapshot.Values); }
        catch (ArgumentException) { throw new ValueResolutionException("DynamicSnapshotInvalid", ""); }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var item in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(item.Name); WriteCanonical(writer, item.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }
}
