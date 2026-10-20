using System.Text.Json;

namespace WorkerDock.Domain.Jobs;

public sealed record JobRevision(Guid JobId, long Revision, string Name, string TemplateId,
    string TemplateVersion, JsonElement Configuration, Guid? ProfileId, ExecutionPolicy Policy,
    JsonElement? DynamicValues = null);
