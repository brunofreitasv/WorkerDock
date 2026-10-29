# Review incidents and deliveries

## Prerequisites

Tenant users can review incidents and deliveries. **Manage** access is required to retry an exhausted delivery. Live processing requires the SQL-backed tenant service, outbox worker, reachable destination, and a working secret provider for authenticated headers.

## Role

The Alerts page separates incidents and delivery attempts. Read users can inspect records and safe result codes. Only Manage users can change rules or sinks and request a retry.

## Steps

1. Open [Alerts](#/alerts). In **Incidents**, review rule, condition, subject, state, last observed time, and safe code.
2. In **Deliveries**, review delivery ID, notification kind, status, attempt count, next attempt time, and safe code. Persisted statuses are **Pending** (waiting for processing), **Leased** (claimed by a worker), **Delivered** (completed successfully), **Failed** (processing ended unsuccessfully), and **Cancelled** (cancelled). The tenant table does not display an incident ID or an “exhausted” status.
3. For a failed delivery, check its safe code, sink host/port policy, provider reference, and receiver health. Use the delivery ID to correlate receiver logs.
4. The tenant UI is read-only for delivery records and has no retry control. An authorized Manage user can request a retry of a **Failed** delivery through the supported API route `POST /api/alerts/deliveries/{id}/retry`, with `AdditionalAttempts` from 1 to 20. The retry preserves the delivery ID and is bounded by retry limits. Check the updated count and status in the tenant table.

Use the incident and delivery history filters to select a period, state, rule, delivery status, or notification kind. Results are ordered by latest incident activity or delivery creation time and are paged independently. Open and Unknown incidents remain visible in the default active view even when they are older than the selected period.

## Example

The delivery table shows `Failed` with safe code `DestinationDenied`. Check the sink URL and `tenant-alerts` policy, approve the needed host and port, then have an authorized Manage user request a retry through the supported API. The same delivery ID lets the receiver correlate the repeated request.

## Verify

Confirm a successful attempt has **Delivered** status and the incident reflects the latest evaluation. The page shows safe codes, not response bodies, exception details, or credentials. The stable `Idempotency-Key` header is the delivery ID.

## Recover

Delivery is **at least once**, not exactly once. A process can send a request and crash before recording acknowledgement, so a later attempt can repeat it. Receivers should deduplicate by `Idempotency-Key` / delivery ID. Before retrying an uncertain outcome, check receiver logs and downstream effects. Missing worker telemetry can leave an incident Unknown; it does not prove recovery.

## Related guides

See [alert rules](#/help:alert-rules), [webhook sinks](#/help:webhook-sinks), [outbound policy](#/help:outbound-policy), and [execution events](#/help:execution-events).
