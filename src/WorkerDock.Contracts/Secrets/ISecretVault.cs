namespace WorkerDock.Contracts.Secrets;
public sealed record SecretReference(string Name);
public interface ISecretVault
{
    ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct);
}
public sealed class SecretValue(ReadOnlySpan<char> value) : IDisposable
{
    private char[]? buffer = value.ToArray();
    public ReadOnlyMemory<char> Reveal() => buffer ?? throw new ObjectDisposedException(nameof(SecretValue));
    public override string ToString() => "[REDACTED]";
    public void Dispose() { if (buffer is { } current) { Array.Clear(current); buffer = null; } }
}
