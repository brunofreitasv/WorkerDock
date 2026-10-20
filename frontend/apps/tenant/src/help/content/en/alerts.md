# Set up alerts

## Prerequisites

Manage permission is required. Before creating an alert, configure the tenant outbound policy to allow the destination host and port, and make sure the secret provider can resolve any logical secret references used for webhook headers. The destination is checked against outbound policy when a delivery runs.

## Role

Manage to create or change webhook sinks and rules, enable rules, or retry an exhausted delivery. Users with Read access can review incidents and deliveries only.

## Steps

1. Open [Alerts](#/alerts) and create a webhook sink. Enter its name and HTTPS URL, optional header names with logical **secret references**, and the retry and timeout settings. Do not enter header secret values. A sink can be disabled before saving; a sink by itself sends no notifications.
2. Create an alert rule and choose an enabled sink. Select a supported condition, then set its threshold, lookback window, grace period, and reminder interval within the field limits. Long-running and worker-unavailable rules require a threshold of one.
3. Save the rule. The first save creates it **disabled**. Review the saved rule, then use **Enable** as a separate explicit action to activate it.
4. Review incidents and delivery attempts in the Alerts page. Delivery status and safe result codes are read-only. A failed or exhausted delivery may be retried by a manager when the control is available; retries preserve the delivery identity and may result in another HTTP request.

Alert evaluation, incident persistence, and delivery processing use tenant SQL and the outbox worker. Memory-mode behavior or a UI save does not prove live SQL evaluation or webhook delivery. The development vault adapter does not provide a production secret service; verify the configured trusted provider before relying on secret-backed delivery. Webhooks are at least once, so receivers should deduplicate with the delivery ID. See [Outbound policy](#/settings) for destination controls.

## Result

An enabled rule can open incidents and queue webhook deliveries through the configured SQL-backed service. Read users can inspect incident and delivery status without seeing or changing sink credentials.
