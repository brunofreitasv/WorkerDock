using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Values;

namespace WorkerDock.Infrastructure.Jobs;

internal static class TemplateJobValidation
{
    public static ValueTask ValidateAsync(ITemplateCatalog? catalog, JobRevision revision, CancellationToken ct) =>
        ValidateAsync(catalog, revision, ct, null);

    public static async ValueTask ValidateAsync(ITemplateCatalog? catalog, JobRevision revision, CancellationToken ct, ITemplateAuthoringService? authoring = null)
        => await ValidateAsync(catalog, revision, ct, authoring, null);

    public static async ValueTask ValidateAsync(ITemplateCatalog? catalog, JobRevision revision, CancellationToken ct, ITemplateAuthoringService? authoring, string? triggerKind)
    {
        DynamicValuePlan? plan = null;
        if (revision.DynamicValues is { } value)
        {
            try { plan = DynamicValueJson.Read(value); }
            catch (ValueResolutionException) { throw new WorkerDockException("ValidationFailed", "configuration.dynamicValues"); }
        }
        await ValidateAsync(catalog, revision.TemplateId, revision.TemplateVersion, revision.Configuration, ct, authoring, plan, triggerKind);
    }

    public static async ValueTask ValidateBindingsAsync(ITemplateCatalog? catalog, JobRevision revision, CancellationToken ct)
    {
        if (revision.DynamicValues is not { } value) return;
        DynamicValuePlan plan;
        try { plan = DynamicValueJson.Read(value); }
        catch (ValueResolutionException) { throw new WorkerDockException("ValidationFailed", "configuration.dynamicValues"); }
        await RequireRegisteredAsync(catalog, revision.TemplateId, revision.TemplateVersion, ct);
        var descriptor = (await catalog!.GetAsync(revision.TemplateId, revision.TemplateVersion, ct))!;
        var issue = DynamicValueValidation.Validate(descriptor, revision.Configuration, plan, "Scheduled").FirstOrDefault();
        if (issue is not null) throw new WorkerDockException("ValidationFailed", issue.Path);
    }

    public static async ValueTask RequireRegisteredAsync(ITemplateCatalog? catalog,string id,string version,CancellationToken ct)
    {
        if(catalog is null) throw new WorkerDockException("TemplateCatalogUnavailable","templateCatalog");
        if(await catalog.GetAsync(id,version,ct) is null || !await catalog.IsSelectableAsync(id,version,ct))
            throw new WorkerDockException("UnknownTemplate","templateId");
    }

    public static async ValueTask ValidateAsync(ITemplateCatalog? catalog, string id, string version, JsonElement configuration, CancellationToken ct, ITemplateAuthoringService? authoring = null, DynamicValuePlan? plan = null, string? triggerKind = null)
    {
        await RequireRegisteredAsync(catalog,id,version,ct);
        var descriptor=(await catalog!.GetAsync(id,version,ct))!;
        var issue=TemplateSchemaValidator.Validate(descriptor,configuration).FirstOrDefault();
        if(issue is not null) throw new WorkerDockException("ValidationFailed",issue.Path);
        if (plan is not null)
        {
            issue = DynamicValueValidation.Validate(descriptor, configuration, plan, triggerKind).FirstOrDefault();
            if (issue is not null) throw new WorkerDockException("ValidationFailed", issue.Path);
        }
        var authoringIssue = (authoring ?? BuiltInAuthoringValidators.Create()).Validate(new TemplateAuthoringContext(descriptor, configuration, plan, null, triggerKind)).FirstOrDefault();
        if (authoringIssue is not null) throw new WorkerDockException("ValidationFailed", authoringIssue.Path);
    }
}
