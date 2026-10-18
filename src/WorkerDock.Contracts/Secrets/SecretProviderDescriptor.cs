using System.Text.Json;
namespace WorkerDock.Contracts.Secrets;

/// <summary>Trusted installed provider metadata; contains no identity credentials or resolved values.</summary>
public sealed record SecretProviderDescriptor(string Id,string Version,string DisplayName,string Description,JsonElement NonsecretOptionsSchema,JsonElement UiHints);
