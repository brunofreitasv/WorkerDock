using System.Text.Json;
using System.Text.Json.Serialization;
using WorkerDock.Application.Common;
using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Execution;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Authentication;
using WorkerDock.Infrastructure.Executions;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.Web.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DynamicValuePreviewRequest(string TemplateId, string TemplateVersion, JsonElement Configuration,
    JsonElement DynamicValues, string TriggerKind, DateTimeOffset SampleAtUtc, DateTimeOffset? ScheduledForUtc,
    DateOnly? ReferenceDateOverride, TimeSpan? JobTimeout);

public sealed record DynamicValuePreviewResponse(IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyDictionary<string, JsonElement> Values, bool IsExample);

public static class DynamicValuePreviewEndpoints
{
    private static readonly Guid SampleExecutionId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const string SampleIdempotencyKey = "preview-idempotency-key";

    public static void MapDynamicValuePreview(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/template-values/preview", async (DynamicValuePreviewRequest request, ITemplateCatalog catalog,
            ITemplateAuthoringService authoring, ExecutionValuePreparation preparation, CancellationToken ct) =>
        {
            if (request.Configuration.ValueKind != JsonValueKind.Object)
                return BadRequest("configuration", "Type");
            if (request.TriggerKind is not ("Scheduled" or "Manual") || request.SampleAtUtc.Offset != TimeSpan.Zero ||
                request.ScheduledForUtc.HasValue && request.ScheduledForUtc.Value.Offset != TimeSpan.Zero ||
                request.TriggerKind == "Scheduled" && (request.ScheduledForUtc is null || request.ReferenceDateOverride is not null) ||
                request.TriggerKind == "Manual" && request.ScheduledForUtc is not null)
                return BadRequest("triggerKind", "Value");

            DynamicValuePlan plan;
            try { plan = DynamicValueJson.Read(request.DynamicValues); }
            catch (ValueResolutionException error) { return BadRequest("configuration.dynamicValues", error.Code); }

            var descriptor = await catalog.GetAsync(request.TemplateId, request.TemplateVersion, ct);
            if (descriptor is null || !await catalog.IsSelectableAsync(request.TemplateId, request.TemplateVersion, ct))
                return Results.Ok(Example([new("$", "TemplateUnavailable")]));

            var initialIssues = authoring.Validate(new TemplateAuthoringContext(descriptor, request.Configuration,
                plan, request.JobTimeout, request.TriggerKind));
            if (initialIssues.Count != 0) return Results.Ok(Example(initialIssues));

            var context = new ExecutionValueContext(1, plan.TimeZoneId, request.TriggerKind,
                request.ScheduledForUtc, request.TriggerKind == "Manual" ? request.SampleAtUtc : null,
                request.ReferenceDateOverride, request.SampleAtUtc);
            var execution = new ExecutionDescriptor(SampleExecutionId, 1, request.TemplateId, request.TemplateVersion,
                SampleIdempotencyKey, request.Configuration, null, null, JsonSerializer.SerializeToElement(new { }), null, context);
            var prepared = preparation.Prepare(execution, plan);
            if (prepared.ErrorCode is { } errorCode)
                return Results.Ok(Example([new("configuration.dynamicValues", errorCode)]));

            var finalIssues = authoring.Validate(new TemplateAuthoringContext(descriptor, prepared.Configuration!.Value,
                plan, request.JobTimeout, request.TriggerKind));
            return Results.Ok(new DynamicValuePreviewResponse(finalIssues, finalIssues.Count == 0 ? prepared.Snapshot!.Values : EmptyValues, true));
        }).RequireAuthorization(AccessPolicies.Manage);
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyValues = new Dictionary<string, JsonElement>();
    private static DynamicValuePreviewResponse Example(IReadOnlyList<ValidationIssue> issues) => new(issues, EmptyValues, true);
    private static IResult BadRequest(string path, string code) => Results.BadRequest(Example([new(path, code)]));
}
