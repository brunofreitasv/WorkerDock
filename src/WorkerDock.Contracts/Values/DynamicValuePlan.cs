using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkerDock.Contracts.Values;

public enum DynamicValueSource { ReferenceDate, ScheduledTimestamp, FirstExecutionStart, ExecutionId, IdempotencyKey }
public enum DynamicValueBoundary { None, StartOfDay, EndOfDay, StartOfMonth, EndOfMonth, StartOfYear, EndOfYear }
public enum DynamicValueFormat { String, IsoDate, IsoOffsetTimestamp, IsoUtcTimestamp, UnixSeconds, UnixMilliseconds }

public sealed record DynamicValueBinding(string Target, DynamicValueSource Source,
    string? TimeZoneId, int Months, int Days, DynamicValueBoundary Boundary, DynamicValueFormat Format);

public sealed record DynamicValuePlan(int ResolverVersion, string TimeZoneId,
    IReadOnlyList<DynamicValueBinding> Bindings);

public static class DynamicValueJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DynamicValuePlan Read(JsonElement value)
    {
        try
        {
            RequireProperties(value, "resolverVersion", "timeZoneId", "bindings");
            foreach (var binding in value.GetProperty("bindings").EnumerateArray())
                RequireProperties(binding, "target", "source", "timeZoneId", "months", "days", "boundary", "format");
            return Freeze(JsonSerializer.Deserialize<DynamicValuePlan>(value, Options)
                ?? throw new JsonException());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        }
    }

    private static void RequireProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            expected.Except(actual, StringComparer.Ordinal).Any()) throw new JsonException();
    }

    public static JsonElement Write(DynamicValuePlan plan) => JsonSerializer.SerializeToElement(Freeze(plan), Options);

    public static DynamicValuePlan Freeze(DynamicValuePlan plan)
    {
        if (plan is null || plan.Bindings is null || plan.TimeZoneId is null)
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        ValidatePlan(plan);
        var bindings = plan.Bindings.Select((binding, index) =>
        {
            if (binding is null) throw Invalid(index);
            ValidateBinding(binding, index);
            return binding with { };
        }).ToArray();
        for (var i = 0; i < bindings.Length; i++)
        for (var j = i + 1; j < bindings.Length; j++)
            if (bindings[i].Target == bindings[j].Target || bindings[i].Target.StartsWith(bindings[j].Target + "/", StringComparison.Ordinal) ||
                bindings[j].Target.StartsWith(bindings[i].Target + "/", StringComparison.Ordinal))
                throw Invalid(j);
        return plan with { Bindings = Array.AsReadOnly(bindings) };
    }

    internal static void ValidatePlan(DynamicValuePlan plan)
    {
        if (plan.ResolverVersion != 1) throw new ValueResolutionException("DynamicResolverUnsupported", "");
        if (plan.Bindings.Count > 128) throw new ValueResolutionException("DynamicBindingInvalid", "");
        if (string.IsNullOrWhiteSpace(plan.TimeZoneId) || plan.TimeZoneId.Length > 100)
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId); }
        catch (TimeZoneNotFoundException) { throw new ValueResolutionException("DynamicTimeZoneInvalid", ""); }
        catch (InvalidTimeZoneException) { throw new ValueResolutionException("DynamicTimeZoneInvalid", ""); }
    }

    internal static void ValidateBinding(DynamicValueBinding binding, int index)
    {
        if (string.IsNullOrEmpty(binding.Target) || binding.Target.Length > 256 || binding.Target[0] != '/' ||
            !ValidPointer(binding.Target) || !Enum.IsDefined(binding.Source) || !Enum.IsDefined(binding.Format) ||
            !Enum.IsDefined(binding.Boundary) || binding.Months is < -1200 or > 1200 || binding.Days is < -36600 or > 36600)
            throw Invalid(index);
        if (binding.TimeZoneId is { Length: > 100 } || binding.TimeZoneId is { Length: 0 }) throw Invalid(index);
        if (binding.TimeZoneId is not null)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(binding.TimeZoneId); }
            catch (TimeZoneNotFoundException) { throw new ValueResolutionException("DynamicTimeZoneInvalid", binding.Target); }
            catch (InvalidTimeZoneException) { throw new ValueResolutionException("DynamicTimeZoneInvalid", binding.Target); }
        }
        var identifier = binding.Source is DynamicValueSource.ExecutionId or DynamicValueSource.IdempotencyKey;
        if (identifier && (binding.Format != DynamicValueFormat.String || binding.Months != 0 || binding.Days != 0 || binding.Boundary != DynamicValueBoundary.None || binding.TimeZoneId is not null))
            throw new ValueResolutionException("DynamicTypeMismatch", binding.Target);
        if (binding.Source == DynamicValueSource.ReferenceDate &&
            (binding.Format != DynamicValueFormat.IsoDate || binding.TimeZoneId is not null))
            throw new ValueResolutionException("DynamicTypeMismatch", binding.Target);
        if ((binding.Source is DynamicValueSource.ScheduledTimestamp or DynamicValueSource.FirstExecutionStart) && binding.Format == DynamicValueFormat.String)
            throw new ValueResolutionException("DynamicTypeMismatch", binding.Target);
    }

    private static bool ValidPointer(string pointer)
    {
        for (var i = 0; i < pointer.Length; i++)
        {
            if (pointer[i] != '~') continue;
            if (++i >= pointer.Length || pointer[i] is not ('0' or '1')) return false;
        }
        return true;
    }

    private static ValueResolutionException Invalid(int index) => new("DynamicBindingInvalid", $"bindings[{index}]");
}
