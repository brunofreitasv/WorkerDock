namespace WorkerDock.Quartz.Hosting;
public sealed record QuartzNodeOptions(Guid InstanceId, string BootId, int HostCeiling)
{
    public bool StartClosed { get; init; }
    public IReadOnlyDictionary<string, int> TemplateLimits { get; init; } = new Dictionary<string, int>();
}
