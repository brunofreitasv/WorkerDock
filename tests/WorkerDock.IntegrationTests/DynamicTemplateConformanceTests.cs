using System.Text.Json;
using WorkerDock.Contracts.Values;
using WorkerDock.Contracts.Templates;
using WorkerDock.Templates.Http;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.IntegrationTests;

public sealed class DynamicTemplateConformanceTests
{
    [Fact]
    public void Http_authoring_accepts_bound_values_and_rejects_missing_placeholders()
    {
        var descriptor = HttpTemplateDescriptors.Get("1.1.0");
        var service = new TemplateAuthoringService([new HttpConfigurationValidator("1.1.0")]);
        using var config = JsonDocument.Parse("""{"method":"GET","url":"https://example.com/{date}","query":{},"headers":{},"secretHeaders":{},"parameters":{},"body":"{\"epoch\":{epoch}}","timeoutSeconds":null,"successStatuses":[],"maxResponseBytes":100,"responseMode":"SafeSummary"}""");
        var plan = new DynamicValuePlan(1, "UTC", [
            new("/parameters/date", DynamicValueSource.ReferenceDate, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate),
            new("/parameters/epoch", DynamicValueSource.FirstExecutionStart, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.UnixSeconds)]);
        Assert.Empty(service.Validate(new(descriptor, config.RootElement, plan, null, "Manual")));
        Assert.Contains(service.Validate(new(descriptor, config.RootElement, null, null, "Manual")), issue => issue.Code == "HttpParameterMissing");
    }

    [Fact]
    public void Same_context_resolves_across_templates()
    {
        var targets = new[] { "/parameters/0/value", "/parameters/referenceDate", "/text" };
        var bindings = targets.Select(target => new DynamicValueBinding(target, DynamicValueSource.ReferenceDate,
            null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate)).ToArray();
        var context = new ExecutionValueContext(1, "UTC", "Manual", null,
            DateTimeOffset.Parse("2026-10-20T15:00:00Z"), DateOnly.Parse("2026-10-20"), null);
        var snapshot = new DynamicValueResolver().Resolve(new(1, "UTC", bindings), context, Guid.Empty, "same-context", new string('A', 64));
        Assert.Equal("2026-10-20", snapshot.Values[targets[0]].GetString());
        Assert.Equal(snapshot.Values[targets[0]].GetString(), snapshot.Values[targets[1]].GetString());
        Assert.Equal(snapshot.Values[targets[0]].GetString(), snapshot.Values[targets[2]].GetString());
    }
}
