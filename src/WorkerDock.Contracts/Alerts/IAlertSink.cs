using System.Text.Json;
using WorkerDock.Contracts.Secrets;

namespace WorkerDock.Contracts.Alerts;

public sealed record AlertEnvelope(Guid DeliveryId, Guid IncidentId, string Kind, DateTimeOffset AtUtc, string SafeSummary, string CorrelationId);

public sealed record AlertDeliveryResult(string Outcome, string SafeCode, TimeSpan? RetryAfter = null)
{
    public static AlertDeliveryResult Accepted(string code = "WebhookAccepted") => new("Accepted", code);
    public static AlertDeliveryResult TransientFailure(string code, TimeSpan? retryAfter = null) => new("TransientFailure", code, retryAfter);
    public static AlertDeliveryResult PermanentFailure(string code) => new("PermanentFailure", code);
}

public interface IAlertSink
{
    string Type { get; }
    ValueTask<AlertDeliveryResult> DeliverAsync(JsonElement configuration, AlertEnvelope alert, ISecretVault secrets, TimeSpan timeout, CancellationToken ct);
}
