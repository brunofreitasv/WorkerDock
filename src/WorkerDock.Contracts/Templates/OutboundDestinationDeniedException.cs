namespace WorkerDock.Contracts.Templates;

public sealed class OutboundDestinationDeniedException(string safeCode) : Exception(safeCode)
{
    public string SafeCode { get; } = safeCode;
}
