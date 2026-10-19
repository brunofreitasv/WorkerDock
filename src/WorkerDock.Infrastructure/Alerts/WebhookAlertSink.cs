using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WorkerDock.Application.Alerts;
using WorkerDock.Application.Outbound;
using WorkerDock.Contracts.Alerts;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Outbound;
using WorkerDock.Application.Settings;

namespace WorkerDock.Infrastructure.Alerts;

public sealed class WebhookAlertSink(IIntegrationSettings integrationSettings) : IAlertSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Type => "webhook";

    public async ValueTask<AlertDeliveryResult> DeliverAsync(JsonElement configuration, AlertEnvelope alert, ISecretVault secrets, TimeSpan timeout, CancellationToken ct)
    {
        if (alert.DeliveryId == Guid.Empty || alert.IncidentId == Guid.Empty || alert.Kind is not ("Open" or "Reminder" or "Resolve") ||
            alert.SafeSummary.Length > 500 || alert.CorrelationId.Length > 100 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            return AlertDeliveryResult.PermanentFailure("WebhookConfigurationInvalid");

        WebhookConfiguration webhook;
        try
        {
            var frozen = AlertSinkValidation.ValidateAndFreeze(new(Guid.NewGuid(), "Webhook", Type, configuration, 1, TimeSpan.Zero, TimeSpan.Zero, timeout, true));
            webhook = JsonSerializer.Deserialize<WebhookConfiguration>(frozen.Configuration.GetRawText(), JsonOptions)!;
            if (webhook?.Url is null) return AlertDeliveryResult.PermanentFailure("WebhookConfigurationInvalid");
        }
        catch { return AlertDeliveryResult.PermanentFailure("WebhookConfigurationInvalid"); }

        try
        {
            var snapshot = await integrationSettings.GetSnapshotAsync(ct);
            var policy = OutboundPolicy.FromIntegrationConfiguration(IntegrationConfigurationJson.Serialize(snapshot));
            var target = new OutboundTarget(webhook.Url, "tenant-alerts");
            await policy.ResolveAsync(target, ct); // Approve the destination before resolving or attaching credentials.
            using var client = new ValidatedConnectionFactory(policy).CreateClient("tenant-alerts");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url);
            request.Headers.TryAddWithoutValidation("X-WorkerDock-Delivery-Id", alert.DeliveryId.ToString("N"));
            request.Headers.TryAddWithoutValidation("Idempotency-Key", alert.DeliveryId.ToString("N"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                deliveryId = alert.DeliveryId,
                incidentId = alert.IncidentId,
                kind = alert.Kind,
                occurredAtUtc = alert.AtUtc.ToUniversalTime(),
                summary = alert.SafeSummary,
                correlationId = alert.CorrelationId
            }, JsonOptions), Encoding.UTF8, "application/json");

            foreach (var header in webhook.Headers ?? Array.Empty<WebhookHeader>())
            {
                string value;
                if (header.SecretReference is { } reference)
                {
                    using var secret = await secrets.ResolveAsync(new SecretReference(reference), timeoutSource.Token);
                    value = new string(secret.Reveal().Span);
                }
                else value = header.Value!;
                if (!request.Headers.TryAddWithoutValidation(header.Name, value)) return AlertDeliveryResult.PermanentFailure("WebhookHeaderInvalid");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (response.IsSuccessStatusCode) return AlertDeliveryResult.Accepted();
            var retry = GetRetryAfter(response.Headers.RetryAfter);
            return (int)response.StatusCode == 429 || (int)response.StatusCode >= 500
                ? AlertDeliveryResult.TransientFailure((int)response.StatusCode == 429 ? "WebhookRateLimited" : "WebhookServerFailure", retry)
                : AlertDeliveryResult.PermanentFailure("WebhookRejected");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return AlertDeliveryResult.TransientFailure("WebhookTimeout"); }
        catch (OutboundPolicyException error) { return AlertDeliveryResult.PermanentFailure(SafeOutboundCode(error.SafeCode)); }
        catch (HttpRequestException error) when (FindDestinationDenial(error) is { } denied)
        { return AlertDeliveryResult.PermanentFailure(SafeOutboundCode(denied.SafeCode)); }
        catch (HttpRequestException) { return AlertDeliveryResult.TransientFailure("WebhookRequestFailed"); }
        catch (SecretUnavailableException) { return AlertDeliveryResult.PermanentFailure("WebhookSecretUnavailable"); }
        catch { return AlertDeliveryResult.TransientFailure("WebhookRequestFailed"); }
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta) return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (value?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static OutboundDestinationDeniedException? FindDestinationDenial(Exception error) =>
        error is OutboundDestinationDeniedException denied ? denied : error.InnerException is null ? null : FindDestinationDenial(error.InnerException);

    private static string SafeOutboundCode(string code) => code is "OutboundHostDenied" or "OutboundPortDenied" or "OutboundAddressDenied" or
        "OutboundTargetInvalid" or "OutboundPolicyUnavailable" or "OutboundPolicyInvalid" or "OutboundDestinationChanged" or "OutboundPeerMismatch"
        ? code : "OutboundDestinationDenied";
}
