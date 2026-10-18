using WorkerDock.Application.Common;
using WorkerDock.Application.Templates;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Infrastructure.Templates;

/// <summary>SQL-independent development catalog. The trust list comes only from installed package composition.</summary>
public sealed class TemplateCatalog : ITemplateCatalog
{
    private readonly HashSet<(string Id,string Version,string Digest)> trusted;
    private readonly Dictionary<(string Id,string Version),TemplateDescriptor> entries=[];
    private readonly HashSet<(string Id,string Version)> retired=[];
    private readonly object gate=new();
    public TemplateCatalog(IReadOnlyList<TemplateDescriptor> trustedInstalled)
        => trusted=trustedInstalled.Select(x=>(x.Id,x.Version,x.MetadataDigest)).ToHashSet();
    internal IReadOnlyList<TemplateDescriptor> Prepare(IReadOnlyList<TemplateDescriptor> descriptors)
    {
        if(descriptors.Count>256 || descriptors.Select(x=>(x.Id,x.Version)).Distinct().Count()!=descriptors.Count)
            throw new WorkerDockException("TemplateRegistrationConflict");
        var copied=new List<TemplateDescriptor>();
        foreach(var descriptor in descriptors)
        {
            if(TemplateSchemaValidator.ValidateDescriptor(descriptor).Count!=0) throw new WorkerDockException("TemplateMetadataInvalid");
            if(TemplateMetadata.ComputeDigest(descriptor)!=descriptor.MetadataDigest) throw new WorkerDockException("TemplateMetadataMismatch");
            if(!trusted.Contains((descriptor.Id,descriptor.Version,descriptor.MetadataDigest))) throw new WorkerDockException("UntrustedTemplate");
            copied.Add(TemplateMetadata.Copy(descriptor));
        }
        return copied;
    }
    public ValueTask<string> RegisterAsync(IReadOnlyList<TemplateDescriptor> descriptors,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var prepared=Prepare(descriptors);
        lock(gate)
        {
            foreach(var d in prepared)
                if(entries.TryGetValue((d.Id,d.Version),out var prior) && prior.MetadataDigest!=d.MetadataDigest) throw new WorkerDockException("MetadataVersionConflict");
            foreach(var d in prepared) entries.TryAdd((d.Id,d.Version),d);
            return ValueTask.FromResult(TemplateMetadata.AggregateDigest(prepared));
        }
    }
    public ValueTask<IReadOnlyList<TemplateDescriptor>> ListAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock(gate) return ValueTask.FromResult<IReadOnlyList<TemplateDescriptor>>(entries.Values.OrderBy(x=>x.Id,StringComparer.Ordinal).ThenBy(x=>x.Version,StringComparer.Ordinal).Select(TemplateMetadata.Copy).ToArray()); }
    public ValueTask<TemplateDescriptor?> GetAsync(string id,string version,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock(gate) return ValueTask.FromResult(entries.TryGetValue((id,version),out var descriptor)?TemplateMetadata.Copy(descriptor):null); }
    public ValueTask RetireAsync(string id,string version,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock(gate)
        { if(!entries.ContainsKey((id,version))) throw new WorkerDockException("TemplateVersionUnavailable"); retired.Add((id,version)); }
        return ValueTask.CompletedTask;
    }
    public ValueTask<bool> IsSelectableAsync(string id,string version,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock(gate) return ValueTask.FromResult(entries.ContainsKey((id,version)) && !retired.Contains((id,version))); }
}
