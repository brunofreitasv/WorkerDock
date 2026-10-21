# Configure webhook sinks

## Prerequisites

You need **Manage** access. Allow the destination host and port in [outbound policy](#/help:outbound-policy), and arrange a trusted secret provider for authenticated headers. A sink defines a destination; notifications are sent only when an enabled rule matches an event.

## Role

Manage users can create, edit, enable, or disable sinks. Read users can review incidents and delivery results but cannot change sinks. Do not put credentials in the URL.

## Steps

1. Open [Alerts](#/alerts) and choose **New webhook sink**. Enter a name and HTTP or HTTPS URL without embedded credentials or a fragment. Prefer HTTPS.
2. For each custom header, enter its header name and logical **secret reference**. The provider resolves it at delivery time; do not enter the value. Restricted transport headers are rejected.
3. Set **Maximum attempts** from 1 to 20, base and maximum retry delays, and timeout from 1 to 120 seconds. Maximum delay must be at least the base delay.
4. Choose **Save** and review the URL, references, retry budget, and enabled state. A sink alone does not create an alert; create a rule and select an enabled sink to route notifications.

## Example

For `hooks.example.net`, first allow that host on port `443` in `tenant-alerts`. Create **Operations notifications**, add `Authorization` with reference `tenant/alerts/ops-token`, and set five attempts, 10-second base delay, 120-second maximum delay, and a 15-second timeout. Keep the sink disabled until the receiver and provider are checked.

## Verify

Confirm the sink appears with the intended enabled state. Link it to a rule after a controlled receiver check. Review delivery status and safe result code in [incidents and deliveries](#/help:incidents-deliveries); response bodies and credentials are not shown.

## Recover

If validation fails, check URL format, header names, duplicate names, non-empty references, timeout, and retry limits. If delivery is denied, check `tenant-alerts` host and port policy. For secret resolution failures, ask the provider administrator to check the reference and service identity. Reload and review changes if the version is stale.

## Related guides

See [alert rules](#/help:alert-rules), [secrets](#/help:secrets), [outbound policy](#/help:outbound-policy), and [incidents and deliveries](#/help:incidents-deliveries).
