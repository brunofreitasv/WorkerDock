using WorkerDock.Contracts.Templates;
namespace WorkerDock.Application.Templates;
public interface ITemplateCatalog
{
    ValueTask<string> RegisterAsync(IReadOnlyList<TemplateDescriptor> descriptors,CancellationToken ct);
    ValueTask<IReadOnlyList<TemplateDescriptor>> ListAsync(CancellationToken ct);
    ValueTask<bool> IsSelectableAsync(string id,string version,CancellationToken ct);
    ValueTask<TemplateDescriptor?> GetAsync(string id,string version,CancellationToken ct);
}
