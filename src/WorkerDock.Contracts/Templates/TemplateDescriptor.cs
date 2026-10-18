using System.Text.Json;
namespace WorkerDock.Contracts.Templates;
public sealed record TemplateDescriptor(string Id, string Version, string DisplayName, string Description, JsonElement ConfigurationSchema, JsonElement UiHints, string MetadataDigest);
public sealed record ValidationIssue(string Path, string Code);
