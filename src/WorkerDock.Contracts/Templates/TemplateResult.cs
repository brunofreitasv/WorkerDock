using System.Text.Json;
namespace WorkerDock.Contracts.Templates;
public sealed record TemplateResult(string Outcome, string SafeCode, bool EffectsMayHaveOccurred, JsonElement SafeSummary, TimeSpan? RetryAfter, FailureDiagnostic? Diagnostic = null);
