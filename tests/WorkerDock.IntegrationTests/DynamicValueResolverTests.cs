using System.Globalization;
using System.Text.Json;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.IntegrationTests;

public sealed class DynamicValueResolverTests
{
    private static DynamicValueBinding Binding(string target, DynamicValueSource source,
        DynamicValueFormat format = DynamicValueFormat.IsoDate, int months = 0, int days = 0,
        DynamicValueBoundary boundary = DynamicValueBoundary.None, string? zone = null) =>
        new(target, source, zone, months, days, boundary, format);

    private static ExecutionValueContext Scheduled(string instant, string zone = "UTC") =>
        new(1, zone, "Scheduled", DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture).ToUniversalTime(), null, null, null);

    private static DynamicValueSnapshot Resolve(DynamicValueBinding binding, ExecutionValueContext context,
        Guid? id = null, string key = "retry-key") =>
        new DynamicValueResolver().Resolve(new(1, context.TimeZoneId, [binding]), context, id ?? Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), key, new string('A', 64));

    [Fact]
    public void Delayed_execution_uses_original_scheduled_date()
    {
        var context = Scheduled("2026-10-20T09:00:00Z", "America/Sao_Paulo") with { FirstStartedAtUtc = DateTimeOffset.Parse("2026-10-21T09:00:00Z") };
        var snapshot = Resolve(Binding("/parameters/0/value", DynamicValueSource.ReferenceDate), context);
        Assert.Equal("2026-10-20", snapshot.Values["/parameters/0/value"].GetString());
    }

    [Fact]
    public void Manual_override_is_a_date_only_value()
    {
        var context = new ExecutionValueContext(1, "UTC", "Manual", null,
            DateTimeOffset.Parse("2026-10-21T18:30:00Z"), DateOnly.Parse("2026-10-19"), null);
        var snapshot = Resolve(Binding("/parameters/0/value", DynamicValueSource.ReferenceDate), context);
        Assert.Equal("2026-10-19", snapshot.Values["/parameters/0/value"].GetString());
    }

    [Theory]
    [InlineData("2024-01-31", 1, 0, "2024-02-29")]
    [InlineData("2024-01-31", 1, 1, "2024-03-01")]
    public void Calendar_offsets_apply_months_then_days(string date, int months, int days, string expected)
    {
        var ctx = Scheduled(date + "T12:00:00Z");
        var snap = Resolve(Binding("/x", DynamicValueSource.ScheduledTimestamp, months: months, days: days), ctx);
        Assert.Equal(expected, snap.Values["/x"].GetString());
    }

    [Theory]
    [InlineData("2026-03-07T02:30:00-05:00")]
    [InlineData("2026-10-31T01:30:00-04:00")]
    public void Shift_into_new_york_gap_or_ambiguous_time_fails(string instant)
    {
        var ctx = Scheduled(instant, "America/New_York");
        var error = Assert.Throws<ValueResolutionException>(() => Resolve(Binding("/x", DynamicValueSource.ScheduledTimestamp,
            DynamicValueFormat.IsoOffsetTimestamp, days: 1, zone: "America/New_York"), ctx));
        Assert.Equal("DynamicLocalTimeInvalid", error.Code);
    }

    [Fact]
    public void Unchanged_ambiguous_timestamp_preserves_its_offset()
    {
        var ctx = Scheduled("2026-11-01T01:30:00-04:00", "America/New_York");
        var snap = Resolve(Binding("/x", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.IsoOffsetTimestamp, zone: "America/New_York"), ctx);
        Assert.Equal("2026-11-01T01:30:00.0000000-04:00", snap.Values["/x"].GetString());
    }

    [Fact]
    public void Manual_execution_cannot_read_scheduled_timestamp()
    {
        var ctx = new ExecutionValueContext(1, "UTC", "Manual", null, DateTimeOffset.Parse("2026-10-20T00:00:00Z"), null, null);
        var error = Assert.Throws<ValueResolutionException>(() => Resolve(Binding("/x", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.IsoOffsetTimestamp), ctx));
        Assert.Equal("DynamicSourceUnavailable", error.Code);
    }

    [Fact]
    public void Snapshot_is_stable_and_json_values_are_frozen()
    {
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var plan = new DynamicValuePlan(1, "UTC", [Binding("/a", DynamicValueSource.ExecutionId, DynamicValueFormat.String), Binding("/b", DynamicValueSource.IdempotencyKey, DynamicValueFormat.String)]);
        var resolver = new DynamicValueResolver();
        var first = resolver.Resolve(plan, Scheduled("2026-10-20T00:00:00Z"), id, "stable", new string('B',64));
        var second = resolver.Resolve(plan, Scheduled("2026-10-20T00:00:00Z"), id, "stable", new string('B',64));
        Assert.Equal(first.Values["/a"].GetString(), second.Values["/a"].GetString());
        Assert.Equal("00112233445566778899aabbccddeeff", first.Values["/a"].GetString());
        Assert.Equal("stable", first.Values["/b"].GetString());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JsonElement>)first.Values).Add("/c", JsonDocument.Parse("1").RootElement));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void Output_is_culture_independent(string culture)
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var ctx = Scheduled("2026-10-20T09:00:00Z");
            var result = Resolve(Binding("/x", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.UnixSeconds), ctx);
            Assert.Equal(JsonValueKind.Number, result.Values["/x"].ValueKind);
            Assert.Equal(1792486800, result.Values["/x"].GetInt64());
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Theory]
    [InlineData(2, "DynamicResolverUnsupported")]
    [InlineData(0, "DynamicResolverUnsupported")]
    public void Unsupported_resolver_version_fails(int version, string code)
    {
        var context = Scheduled("2026-10-20T09:00:00Z");
        var plan = new DynamicValuePlan(version, "UTC", []);
        var error = Assert.Throws<ValueResolutionException>(() => new DynamicValueResolver().Resolve(plan, context, Guid.Empty, "key", new string('A',64)));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Calendar_fixture_cases_and_boundaries_match_contract()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "dynamic-values.json")));
        foreach (var row in fixture.RootElement.GetProperty("calendarCases").EnumerateArray())
        {
            var date = row.GetProperty("source").GetString()!;
            var context = Scheduled(date + "T12:00:00Z");
            var binding = Binding("/value", DynamicValueSource.ScheduledTimestamp, months: row.GetProperty("months").GetInt32(), days: row.GetProperty("days").GetInt32());
            Assert.Equal(row.GetProperty("expected").GetString(), Resolve(binding, context).Values["/value"].GetString());
        }

        foreach (var row in fixture.RootElement.GetProperty("boundaries").EnumerateArray())
        {
            var source = row.GetProperty("source").GetString()!;
            var boundary = Enum.Parse<DynamicValueBoundary>(row.GetProperty("boundary").GetString()!);
            var isDate = !source.Contains('T');
            var context = isDate
                ? new ExecutionValueContext(1, "UTC", "Manual", null, DateTimeOffset.Parse(source + "T12:00:00Z"), DateOnly.Parse(source), null)
                : Scheduled(source);
            var binding = Binding("/end", isDate ? DynamicValueSource.ReferenceDate : DynamicValueSource.ScheduledTimestamp,
                isDate ? DynamicValueFormat.IsoDate : DynamicValueFormat.IsoOffsetTimestamp, boundary: boundary);
            Assert.Equal(row.GetProperty("expected").GetString(), Resolve(binding, context).Values["/end"].GetString());
        }
    }

    [Fact]
    public void Every_output_format_has_the_contract_json_type()
    {
        var context = Scheduled("2026-10-20T09:00:00Z");
        var plan = new DynamicValuePlan(1, "UTC", [
            Binding("/date", DynamicValueSource.ReferenceDate),
            Binding("/timestampDate", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.IsoDate),
            Binding("/offset", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.IsoOffsetTimestamp),
            Binding("/utc", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.IsoUtcTimestamp),
            Binding("/seconds", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.UnixSeconds),
            Binding("/milliseconds", DynamicValueSource.ScheduledTimestamp, DynamicValueFormat.UnixMilliseconds),
            Binding("/id", DynamicValueSource.ExecutionId, DynamicValueFormat.String),
            Binding("/key", DynamicValueSource.IdempotencyKey, DynamicValueFormat.String)
        ]);
        var result = new DynamicValueResolver().Resolve(plan, context with { ReferenceDateOverride = DateOnly.Parse("2026-10-19") }, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "retry-key", new string('A', 64));
        Assert.All(new[] { "/date", "/timestampDate", "/offset", "/utc", "/id", "/key" }, key => Assert.Equal(JsonValueKind.String, result.Values[key].ValueKind));
        Assert.Equal(JsonValueKind.Number, result.Values["/seconds"].ValueKind);
        Assert.Equal(JsonValueKind.Number, result.Values["/milliseconds"].ValueKind);
        Assert.Equal("2026-10-19", result.Values["/date"].GetString());
        Assert.Equal("2026-10-20T09:00:00.0000000Z", result.Values["/utc"].GetString());
    }

    [Fact]
    public void Dynamic_value_json_uses_exact_enum_names_and_rejects_unknown_members()
    {
        using var doc = JsonDocument.Parse("""{"resolverVersion":1,"timeZoneId":"UTC","bindings":[{"target":"/x","source":"ReferenceDate","timeZoneId":null,"months":0,"days":0,"boundary":"None","format":"IsoDate"}]}""");
        var plan = DynamicValueJson.Read(doc.RootElement);
        Assert.Equal("ReferenceDate", DynamicValueJson.Write(plan).GetProperty("bindings")[0].GetProperty("source").GetString());
        using var unknown = JsonDocument.Parse("""{"resolverVersion":1,"timeZoneId":"UTC","bindings":[],"extra":true}""");
        Assert.Equal("DynamicBindingInvalid", Assert.Throws<ValueResolutionException>(() => DynamicValueJson.Read(unknown.RootElement)).Code);
        using var missing = JsonDocument.Parse("""{"resolverVersion":1,"timeZoneId":"UTC","bindings":[{"target":"/x","source":"ReferenceDate","timeZoneId":null,"months":0,"days":0,"boundary":"None"}]}""");
        Assert.Equal("DynamicBindingInvalid", Assert.Throws<ValueResolutionException>(() => DynamicValueJson.Read(missing.RootElement)).Code);
    }

    [Fact]
    public void Out_of_range_calendar_operations_fail_with_safe_code()
    {
        var context = new ExecutionValueContext(1, "UTC", "Manual", null, DateTimeOffset.Parse("9999-12-31T00:00:00Z"), DateOnly.MaxValue, null);
        var error = Assert.Throws<ValueResolutionException>(() => Resolve(Binding("/x", DynamicValueSource.ReferenceDate, days: 1), context));
        Assert.Equal("DynamicDateOutOfRange", error.Code);
    }

    [Fact]
    public void Invalid_timezone_and_non_utc_context_instant_are_rejected()
    {
        var invalidZone = new DynamicValuePlan(1, "Mars/Olympus", []);
        Assert.Equal("DynamicTimeZoneInvalid", Assert.Throws<ValueResolutionException>(() => DynamicValueJson.Freeze(invalidZone)).Code);
        var invalidOffset = new ExecutionValueContext(1, "UTC", "Scheduled", DateTimeOffset.Parse("2026-10-20T09:00:00+01:00"), null, null, null);
        Assert.Equal("DynamicBindingInvalid", Assert.Throws<ValueResolutionException>(() => Resolve(Binding("/x", DynamicValueSource.ReferenceDate), invalidOffset)).Code);
    }
}
