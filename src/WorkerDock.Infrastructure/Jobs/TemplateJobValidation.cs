using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Application.Templates;
using WorkerDock.Domain.Jobs;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.Infrastructure.Jobs;

internal static class TemplateJobValidation
{
    public static ValueTask ValidateAsync(ITemplateCatalog? catalog, JobRevision revision, CancellationToken ct) =>
        ValidateAsync(catalog, revision.TemplateId, revision.TemplateVersion, revision.Configuration, ct);

    public static async ValueTask RequireRegisteredAsync(ITemplateCatalog? catalog,string id,string version,CancellationToken ct)
    {
        if(catalog is null) throw new WorkerDockException("TemplateCatalogUnavailable","templateCatalog");
        if(await catalog.GetAsync(id,version,ct) is null || !await catalog.IsSelectableAsync(id,version,ct))
            throw new WorkerDockException("UnknownTemplate","templateId");
    }

    public static async ValueTask ValidateAsync(ITemplateCatalog? catalog, string id, string version, JsonElement configuration, CancellationToken ct)
    {
        await RequireRegisteredAsync(catalog,id,version,ct);
        var descriptor=(await catalog!.GetAsync(id,version,ct))!;
        var issue=TemplateSchemaValidator.Validate(descriptor,configuration).FirstOrDefault();
        if(issue is not null) throw new WorkerDockException("ValidationFailed",issue.Path);
    }
}
