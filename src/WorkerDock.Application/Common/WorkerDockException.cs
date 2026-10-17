namespace WorkerDock.Application.Common;
public sealed class WorkerDockException(string code, string? field = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? Field { get; } = field;
}
