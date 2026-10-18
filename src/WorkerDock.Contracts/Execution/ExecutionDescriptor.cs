using System.Text.Json;
namespace WorkerDock.Contracts.Execution;
public sealed record ExecutionDescriptor(Guid ExecutionId, int AttemptOrdinal, string TemplateId, string TemplateVersion, string IdempotencyKey, JsonElement Configuration, Guid? ProfileRevisionId, JsonElement? ProfileConfiguration, JsonElement IntegrationConfiguration, DateTimeOffset? DeadlineUtc);
