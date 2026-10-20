using System.Text;
using System.Text.Json;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.Infrastructure.Values;

/// <summary>Validates binding declarations against a template's closed schema without resolving values.</summary>
public static class DynamicValueValidation
{
    public static bool ValidateResolvedSnapshot(TemplateDescriptor descriptor, ExecutionDescriptor execution)
    {
        var context = execution.ValueContext;
        var snapshot = execution.ValueSnapshot;
        if (context is null || snapshot is null || context.ResolverVersion != 1 || snapshot.ResolverVersion != 1 ||
            context.FirstStartedAtUtc is null || context.FirstStartedAtUtc.Value.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(context.TimeZoneId) || context.TimeZoneId.Length > 100 ||
            snapshot.InputDigest is not { Length: 64 } digest ||
            digest.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            snapshot.Values is null || snapshot.Values.Count > 128 ||
            context.TriggerKind switch
            {
                "Scheduled" => context.ScheduledForUtc is null || context.ScheduledForUtc.Value.Offset != TimeSpan.Zero ||
                    context.RequestedAtUtc is not null || context.ReferenceDateOverride is not null,
                "Manual" => context.RequestedAtUtc is null || context.RequestedAtUtc.Value.Offset != TimeSpan.Zero ||
                    context.ScheduledForUtc is not null,
                _ => true
            }) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(context.TimeZoneId); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
        try
        {
            foreach (var (pointer, value) in snapshot.Values)
            {
                if (!TryResolve(descriptor.ConfigurationSchema, execution.Configuration, pointer, out var schema, out var missing, out var sensitive) ||
                    missing || sensitive || !IsEligible(schema) || !DynamicValueTargets.TrySegments(pointer, out var segments)) return false;
                var actual = execution.Configuration;
                foreach (var segment in segments)
                {
                    if (actual.ValueKind == JsonValueKind.Object && actual.TryGetProperty(segment, out var property)) actual = property;
                    else if (actual.ValueKind == JsonValueKind.Array && Index(segment, out var index) && index < actual.GetArrayLength()) actual = actual[index];
                    else return false;
                }
                if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array ||
                    !string.Equals(value.GetRawText(), actual.GetRawText(), StringComparison.Ordinal)) return false;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException) { return false; }
        return true;
    }

    public static IReadOnlyList<ValidationIssue> Validate(TemplateDescriptor descriptor, JsonElement configuration, DynamicValuePlan plan, string? triggerKind)
    {
        var issues = new List<ValidationIssue>();
        try
        {
            var frozen = DynamicValueJson.Freeze(plan);
            if (Encoding.UTF8.GetByteCount(configuration.GetRawText()) + Encoding.UTF8.GetByteCount(DynamicValueJson.Write(frozen).GetRawText()) > 49_152)
                return [new("configuration.dynamicValues", "ConfigurationTooLarge")];
            var supplied = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < frozen.Bindings.Count && issues.Count < 64; i++)
            {
                var binding = frozen.Bindings[i];
                if (binding.Source == DynamicValueSource.ScheduledTimestamp && !string.Equals(triggerKind, "Scheduled", StringComparison.OrdinalIgnoreCase))
                { issues.Add(new($"configuration.dynamicValues[{i}]", "DynamicSourceUnavailable")); continue; }
                if (!TryResolve(descriptor.ConfigurationSchema, configuration, binding.Target, out var targetSchema, out var missingLeaf, out var sensitive) || sensitive || !IsEligible(targetSchema))
                { issues.Add(new($"configuration.dynamicValues[{i}]", "DynamicTargetInvalid")); continue; }
                if (missingLeaf) supplied.Add(binding.Target);
                if (!Compatible(targetSchema, binding)) issues.Add(new($"configuration.dynamicValues[{i}]", "DynamicTypeMismatch"));
            }
            issues.AddRange(TemplateSchemaValidator.ValidateAuthoring(descriptor, configuration, supplied).Take(Math.Max(0, 64 - issues.Count)));
        }
        catch (ValueResolutionException ex)
        {
            var code = ex.Code is "DynamicResolverUnsupported" or "DynamicTimeZoneInvalid" or "DynamicTypeMismatch" ? ex.Code : "DynamicBindingInvalid";
            var path = ex.Target.StartsWith("bindings[", StringComparison.Ordinal) && ex.Target.EndsWith(']') ? "configuration.dynamicValues." + ex.Target : "configuration.dynamicValues";
            issues.Add(new(path, code));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        { issues.Add(new("configuration.dynamicValues", "DynamicBindingInvalid")); }
        return issues.Take(64).ToArray();
    }

    private static bool Compatible(JsonElement schema, DynamicValueBinding binding)
    {
        if (!schema.TryGetProperty("type", out var type)) return false;
        var expected = binding.Format is DynamicValueFormat.UnixSeconds or DynamicValueFormat.UnixMilliseconds ? "integer" : "string";
        if (type.ValueKind == JsonValueKind.String) return type.GetString() == expected;
        return type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(x => x.GetString() == expected);
    }

    private static bool IsEligible(JsonElement schema) => schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("x-workerDock-dynamicValue", out var flag) && flag.ValueKind == JsonValueKind.True &&
        schema.TryGetProperty("type", out var type) && IsScalarType(type) &&
        !(schema.TryGetProperty("x-workerDock-sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True) &&
        !(schema.TryGetProperty("x-workerDock-secretReference", out var secret) && secret.ValueKind == JsonValueKind.True);

    private static bool IsScalarType(JsonElement type) => type.ValueKind == JsonValueKind.String
        ? type.GetString() is "string" or "integer" or "number" or "boolean"
        : type.ValueKind == JsonValueKind.Array && type.GetArrayLength() > 0 &&
          type.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString() is "string" or "integer" or "number" or "boolean" or "null");

    private static bool TryResolve(JsonElement rootSchema, JsonElement config, string pointer, out JsonElement finalSchema, out bool missingLeaf, out bool sensitive)
    {
        finalSchema = default; missingLeaf = false; sensitive = false;
        if (!DynamicValueTargets.TrySegments(pointer, out var segments)) return false;
        var schema = rootSchema; var value = config;
        for (var i = 0; i < segments.Length; i++)
        {
            if (schema.ValueKind != JsonValueKind.Object) return false;
            if (schema.TryGetProperty("$ref", out var reference))
            {
                var name = reference.GetString();
                if (name is null || !name.StartsWith("#/$defs/", StringComparison.Ordinal) || !rootSchema.TryGetProperty("$defs", out var defs) || !defs.TryGetProperty(name[8..], out schema)) return false;
            }
            if ((schema.TryGetProperty("x-workerDock-sensitive", out var s) && s.ValueKind == JsonValueKind.True) ||
                (schema.TryGetProperty("x-workerDock-secretReference", out var r) && r.ValueKind == JsonValueKind.True)) sensitive = true;
            if (schema.TryGetProperty("oneOf", out var alternatives))
            {
                var selected = alternatives.EnumerateArray().Where(branch => MatchesBranch(branch, value)).Take(2).ToArray();
                if (selected.Length != 1) return false;
                schema = selected[0];
            }
            var segment = segments[i];
            if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "array")
            {
                if (!Index(segment, out var index) || !schema.TryGetProperty("items", out var item)) return false;
                if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength())
                { if (i == segments.Length - 1) return false; return false; }
                schema = item; value = value[index];
            }
            else
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(segment, out var child))
                {
                    schema = child;
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out var next))
                    { if (i == segments.Length - 1) missingLeaf = true; else return false; }
                    else value = next;
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
                {
                    schema = additional;
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out var next))
                    { if (i == segments.Length - 1) missingLeaf = true; else return false; }
                    else value = next;
                }
                else return false;
            }
            if (missingLeaf && i != segments.Length - 1) return false;
        }
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var finalReference))
        {
            var name = finalReference.GetString();
            if (name is null || !name.StartsWith("#/$defs/", StringComparison.Ordinal) || !rootSchema.TryGetProperty("$defs", out var finalDefs) || !finalDefs.TryGetProperty(name[8..], out schema)) return false;
            if ((schema.TryGetProperty("x-workerDock-sensitive", out var finalSensitive) && finalSensitive.ValueKind == JsonValueKind.True) ||
                (schema.TryGetProperty("x-workerDock-secretReference", out var finalSecret) && finalSecret.ValueKind == JsonValueKind.True)) sensitive = true;
        }
        finalSchema = schema;
        if (missingLeaf) return true;
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
    }

    private static bool MatchesBranch(JsonElement branch, JsonElement value)
    {
        if (branch.ValueKind != JsonValueKind.Object || !branch.TryGetProperty("properties", out var properties)) return false;
        foreach (var prop in properties.EnumerateObject())
            if (prop.Value.TryGetProperty("const", out var constant))
                return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(prop.Name, out var actual) && JsonElement.DeepEquals(actual, constant);
        return false;
    }

    private static bool Index(string segment, out int index)
    {
        index = 0;
        return segment.Length > 0 && (segment == "0" || segment[0] is >= '1' and <= '9') && segment.All(char.IsAsciiDigit) && int.TryParse(segment, out index);
    }
}
