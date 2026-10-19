namespace WorkerDock.ControlCenter.Health;

public sealed class CentralHealthOptions
{
    public string[] AllowedHosts { get; set; } = [];
    public string[] AllowedPrivateCidrs { get; set; } = [];
}
