using WorkerDock.Contracts.Secrets;
namespace WorkerDock.Infrastructure.Secrets;

/// <summary>Development adapter; logical references never select environment variables directly.</summary>
public sealed class EnvironmentSecretVault : ISecretVault
{
    private readonly IReadOnlyDictionary<string,string> approved;
    private readonly Func<string,string?> read;
    public EnvironmentSecretVault(IReadOnlyDictionary<string,string> approvedReferences, Func<string,string?>? read = null)
    {
        if (approvedReferences.Count > 128 || approvedReferences.Any(x => !Name(x.Key) || !Name(x.Value)))
            throw new InvalidOperationException("SecretConfigurationInvalid");
        approved = new Dictionary<string,string>(approvedReferences,StringComparer.Ordinal);
        this.read = read ?? Environment.GetEnvironmentVariable;
    }
    public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference is null || reference.Name is null || !approved.TryGetValue(reference.Name,out var variable))
            throw new SecretUnavailableException();
        try
        {
            var value = read(variable);
            if (string.IsNullOrEmpty(value) || value.Length > 16384) throw new SecretUnavailableException();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SecretValue(value.AsSpan()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch { throw new SecretUnavailableException(); }
    }
    private static bool Name(string value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
