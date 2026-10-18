namespace WorkerDock.Contracts.Secrets;
public sealed class SecretUnavailableException() : Exception("SecretUnavailable")
{
    public string SafeCode => "SecretUnavailable";
}
