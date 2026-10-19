# Alerts

WorkerDock evaluates alert rules against tenant SQL Server data and writes incident and delivery intent in the same transaction. Quartz projects the opaque delivery reference from the existing outbox. There is no external broker.

## Conditions and evaluation

Rules support repeated failures, exhausted retries, timeouts, long-running executions, missed schedules, and unavailable workers. Count-based conditions use the configured threshold within the lookback window. Long-running and unavailable-worker conditions use the window as an age threshold and require a threshold of one. Grace delays opening a new incident; a positive reminder interval schedules reminders while it remains open. Incidents are deduplicated by rule, condition, and subject. Missing worker telemetry produces an Unknown incident state; absent telemetry is never treated as proof of recovery.

The evaluator uses bounded batches and a SQL-backed rule-and-subject cursor. It uses SQL Server UTC time and stores event timestamps in UTC. Rules and sinks retain immutable revisions; each delivery pins the sink revision active when it was created.

## Webhook sink and secrets

Webhook configuration contains an HTTPS destination and optional header names whose values are secret references. Inline header values, credentials in URLs, transport-controlled headers, and secret values in the database are rejected. At send time the delivery worker resolves references through `ISecretVault`. The scheduler currently registers the development vault adapter, which is unavailable unless its development-only environment mapping is explicitly enabled. No production vault adapter is included, so live secret-backed delivery remains unverified until a trusted provider is integrated and tested.

Destinations are checked against the tenant outbound policy at execution time. Configure that policy with explicit host and port allowlists. Private, loopback, link-local, and otherwise unsafe destinations are denied by default to reduce SSRF risk. Keep vault identities and webhook credentials scoped to the tenant.

The webhook payload contains the delivery ID, incident ID, notification kind, UTC timestamp, safe summary, and correlation ID. It contains no job configuration, secret reference, credential, or raw execution error.

## Delivery guarantees and operations

SQL leases use generation fencing and renew while a bounded webhook call runs. A crashed process may have sent a request before losing its lease, so a later attempt can send the same logical delivery again. Delivery is at least once; it is not exactly once. Receivers should deduplicate using the stable `Idempotency-Key` header, which is the delivery ID. Every attempt is recorded with a safe result code. Retry delays are bounded, include deterministic jitter, and respect the configured attempt budget. Operators with management permission can retry an exhausted delivery with a bounded additional budget; this preserves the delivery ID and writes an audit event.

Only users with the management permission can edit rules and sinks or retry a delivery. Users who can access the tenant panel can read incident and delivery views. All mutations use the existing CSRF and same-origin checks. Webhook errors shown in the panel are sanitized result codes, not response bodies or exception text.

## Database setup

Apply tenant migration `0007_alerts` after the preceding tenant migrations using the database migrator. The matching rollback is `database/tenant/0007_alerts.down.sql`; it refuses to drop populated alert tables. The migration manifest contains SHA-256 checksums for both scripts. The database principal needs the migration permissions plus the runtime table grants declared at the end of the up migration.

For local development, use the [in-memory tutorial](tutorials/run-in-memory.md); it does not exercise SQL alert evaluation, leases, or outbox recovery. To verify delivery recovery, configure `WORKERDOCK_TEST_SQL` with a disposable SQL Server 2022+ instance as described in [SQL setup](how-to/setup-sql-server.md), then run `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~AlertDeliveryTests`. Migration checks that do not need SQL Server are `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~CustomSqlSchemaTests|FullyQualifiedName~MigrationInputTests'`. Never put credentials in source control or test output.

## Adding another sink

Implement `IAlertSink` and register the trusted .NET implementation in the host composition root. Validate its public configuration before persistence, keep credentials as vault references, bound its timeout and response handling, return only a safe result code and optional bounded retry delay, and add focused retry and redaction tests. Dashboard users cannot upload executable code.
