using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WorkerDock.Contracts.Values;

namespace WorkerDock.Infrastructure.Values;

/// <summary>Resolves pinned dynamic inputs without consulting process state or external services.</summary>
public sealed class DynamicValueResolver
{
    private const int MaximumSnapshotBytes = 16_384;
    private const int MaximumIdempotencyKeyLength = 100;

    public DynamicValueSnapshot Resolve(DynamicValuePlan plan, ExecutionValueContext context,
        Guid executionId, string idempotencyKey, string inputDigest)
    {
        ArgumentNullException.ThrowIfNull(context);
        plan = DynamicValueJson.Freeze(plan);
        if (context.ResolverVersion != 1) throw new ValueResolutionException("DynamicResolverUnsupported", "");
        if (string.IsNullOrWhiteSpace(context.TimeZoneId) || context.TimeZoneId.Length > 100)
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        if (!string.Equals(plan.TimeZoneId, context.TimeZoneId, StringComparison.Ordinal))
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        if (new[] { context.ScheduledForUtc, context.RequestedAtUtc, context.FirstStartedAtUtc }
            .Any(value => value.HasValue && value.Value.Offset != TimeSpan.Zero))
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        var contextZone = FindZone(context.TimeZoneId, "");
        if (idempotencyKey is null || idempotencyKey.Length is 0 or > MaximumIdempotencyKeyLength)
            throw new ValueResolutionException("DynamicBindingInvalid", "");
        if (inputDigest is null || inputDigest.Length != 64 || inputDigest.Any(c => !Uri.IsHexDigit(c)))
            throw new ValueResolutionException("DynamicBindingInvalid", "");

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (binding, index) in plan.Bindings.Select((value, index) => (value, index)))
        {
            try
            {
                if (values.ContainsKey(binding.Target)) throw new ValueResolutionException("DynamicBindingInvalid", $"bindings[{index}]");
                values.Add(binding.Target, ResolveValue(binding, plan, context, contextZone, executionId, idempotencyKey));
            }
            catch (ValueResolutionException ex) when (ex.Target.Length == 0)
            {
                throw new ValueResolutionException(ex.Code, binding.Target);
            }
        }

        var frozen = new ReadOnlyDictionary<string, JsonElement>(values);
        var snapshot = new DynamicValueSnapshot(1, inputDigest.ToUpperInvariant(), frozen);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot).Length;
        if (bytes > MaximumSnapshotBytes) throw new ValueResolutionException("DynamicSnapshotTooLarge", "");
        return snapshot;
    }

    private static JsonElement ResolveValue(DynamicValueBinding binding, DynamicValuePlan plan,
        ExecutionValueContext context, TimeZoneInfo contextZone, Guid executionId, string idempotencyKey)
    {
        if (binding.Source == DynamicValueSource.ExecutionId)
            return JsonSerializer.SerializeToElement(executionId.ToString("N", CultureInfo.InvariantCulture));
        if (binding.Source == DynamicValueSource.IdempotencyKey)
            return JsonSerializer.SerializeToElement(idempotencyKey);

        if (binding.Source == DynamicValueSource.ReferenceDate)
        {
            var date = GetReferenceDate(context, contextZone, binding.Target);
            date = ShiftDate(date, binding);
            date = ApplyDateBoundary(date, binding.Boundary, binding.Target);
            return JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var instant = binding.Source switch
        {
            DynamicValueSource.ScheduledTimestamp when string.Equals(context.TriggerKind, "Scheduled", StringComparison.OrdinalIgnoreCase)
                => context.ScheduledForUtc,
            DynamicValueSource.ScheduledTimestamp => null,
            DynamicValueSource.FirstExecutionStart => context.FirstStartedAtUtc,
            _ => null
        };
        if (instant is null) throw new ValueResolutionException("DynamicSourceUnavailable", binding.Target);
        var zone = binding.TimeZoneId is null ? contextZone : FindZone(binding.TimeZoneId, binding.Target);
        var transformed = binding.Months != 0 || binding.Days != 0 || binding.Boundary != DynamicValueBoundary.None;
        var value = instant.Value;
        if (transformed)
        {
            var local = TimeZoneInfo.ConvertTime(value, zone).DateTime;
            local = ShiftLocal(local, binding);
            local = ApplyTimestampBoundary(local, binding.Boundary, binding.Target);
            value = CreateLocalInstant(local, zone, binding.Target);
        }
        return FormatTimestamp(value, zone, binding.Format, binding.Target);
    }

    private static DateOnly GetReferenceDate(ExecutionValueContext context, TimeZoneInfo zone, string target)
    {
        if (context.ReferenceDateOverride is { } referenceDate) return referenceDate;
        if (string.Equals(context.TriggerKind, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            if (context.RequestedAtUtc is not { } requested) throw new ValueResolutionException("DynamicSourceUnavailable", target);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(requested, zone).DateTime);
        }
        if (string.Equals(context.TriggerKind, "Scheduled", StringComparison.OrdinalIgnoreCase))
        {
            if (context.ScheduledForUtc is not { } scheduled) throw new ValueResolutionException("DynamicSourceUnavailable", target);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(scheduled, zone).DateTime);
        }
        throw new ValueResolutionException("DynamicSourceUnavailable", target);
    }

    private static DateOnly ShiftDate(DateOnly date, DynamicValueBinding binding)
    {
        try { return date.AddMonths(binding.Months).AddDays(binding.Days); }
        catch (ArgumentOutOfRangeException) { throw new ValueResolutionException("DynamicDateOutOfRange", binding.Target); }
    }

    private static DateOnly ApplyDateBoundary(DateOnly date, DynamicValueBoundary boundary, string target)
    {
        try
        {
            return boundary switch
            {
                DynamicValueBoundary.None or DynamicValueBoundary.StartOfDay or DynamicValueBoundary.EndOfDay => date,
                DynamicValueBoundary.StartOfMonth => new DateOnly(date.Year, date.Month, 1),
                DynamicValueBoundary.EndOfMonth => new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)),
                DynamicValueBoundary.StartOfYear => new DateOnly(date.Year, 1, 1),
                DynamicValueBoundary.EndOfYear => new DateOnly(date.Year, 12, 31),
                _ => throw new ValueResolutionException("DynamicBindingInvalid", target)
            };
        }
        catch (ArgumentOutOfRangeException) { throw new ValueResolutionException("DynamicDateOutOfRange", target); }
    }

    private static DateTime ShiftLocal(DateTime local, DynamicValueBinding binding)
    {
        try
        {
            var date = DateOnly.FromDateTime(local).AddMonths(binding.Months).AddDays(binding.Days);
            return DateTime.SpecifyKind(date.ToDateTime(TimeOnly.FromDateTime(local)), DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException) { throw new ValueResolutionException("DynamicDateOutOfRange", binding.Target); }
    }

    private static DateTime ApplyTimestampBoundary(DateTime local, DynamicValueBoundary boundary, string target)
    {
        var date = DateOnly.FromDateTime(local);
        try
        {
            return boundary switch
            {
                DynamicValueBoundary.None => local,
                DynamicValueBoundary.StartOfDay => date.ToDateTime(TimeOnly.MinValue),
                DynamicValueBoundary.EndOfDay => date.AddDays(1).ToDateTime(TimeOnly.MinValue),
                DynamicValueBoundary.StartOfMonth => new DateOnly(date.Year, date.Month, 1).ToDateTime(TimeOnly.MinValue),
                DynamicValueBoundary.EndOfMonth => new DateOnly(date.Year, date.Month, 1).AddMonths(1).ToDateTime(TimeOnly.MinValue),
                DynamicValueBoundary.StartOfYear => new DateOnly(date.Year, 1, 1).ToDateTime(TimeOnly.MinValue),
                DynamicValueBoundary.EndOfYear => new DateOnly(date.Year + 1, 1, 1).ToDateTime(TimeOnly.MinValue),
                _ => throw new ValueResolutionException("DynamicBindingInvalid", target)
            };
        }
        catch (ArgumentOutOfRangeException) { throw new ValueResolutionException("DynamicDateOutOfRange", target); }
    }

    private static DateTimeOffset CreateLocalInstant(DateTime local, TimeZoneInfo zone, string target)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new ValueResolutionException("DynamicLocalTimeInvalid", target);
        try { return new DateTimeOffset(local, zone.GetUtcOffset(local)); }
        catch (ArgumentException) { throw new ValueResolutionException("DynamicDateOutOfRange", target); }
    }

    private static JsonElement FormatTimestamp(DateTimeOffset value, TimeZoneInfo zone,
        DynamicValueFormat format, string target)
    {
        return format switch
        {
            DynamicValueFormat.IsoDate => JsonSerializer.SerializeToElement(
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            DynamicValueFormat.IsoOffsetTimestamp => JsonSerializer.SerializeToElement(TimeZoneInfo.ConvertTime(value, zone).ToString("O", CultureInfo.InvariantCulture)),
            DynamicValueFormat.IsoUtcTimestamp => JsonSerializer.SerializeToElement(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)),
            DynamicValueFormat.UnixSeconds => JsonSerializer.SerializeToElement(value.ToUnixTimeSeconds()),
            DynamicValueFormat.UnixMilliseconds => JsonSerializer.SerializeToElement(value.ToUnixTimeMilliseconds()),
            _ => throw new ValueResolutionException("DynamicTypeMismatch", target)
        };
    }

    private static TimeZoneInfo FindZone(string zoneId, string target)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (TimeZoneNotFoundException) { throw new ValueResolutionException("DynamicTimeZoneInvalid", target); }
        catch (InvalidTimeZoneException) { throw new ValueResolutionException("DynamicTimeZoneInvalid", target); }
    }
}
