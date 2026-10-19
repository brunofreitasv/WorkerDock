# Retention and archive operations

WorkerDock keeps safe execution logs, execution history and audit history for independently configurable periods. The initial policy defaults are 30, 90 and 365 days respectively. Each period accepts an integer from 1 through 3650 days. Archive-before-delete is disabled by default. Archive file retention is configured separately and continues to apply to existing archives after new archiving is disabled; set it to zero to retain archive files indefinitely.

## Settings API

The tenant UI reads and updates `GET /api/settings/retention` and `PUT /api/settings/retention` on the same origin. The response contains `logDays`, `executionDays`, `auditDays`, `archiveBeforeDelete` and `archiveDays`. `archiveDays` remains independently configurable when archive-before-delete is off, so operators can expire files created earlier. The server supplies the current ETag; updates must send it in `If-Match`. A stale version returns `412 Precondition Failed`; reload and review the current settings before retrying. The browser never sends an archive path. Runtime archive root and service identity are deployment configuration and must not be exposed as user-managed settings.

Only a manager can update this policy. The API validates all day counts, audits the change and applies the same row-version concurrency rules as other settings. Authorization and CSRF protections apply to the update.

## Local mock mode

For a UI-only development run before the API is available, serve the tenant frontend with a same-origin mock that responds to `GET /api/settings/retention` with JSON such as:

```json
{
  "logDays": 30,
  "executionDays": 90,
  "auditDays": 365,
  "archiveBeforeDelete": false,
  "archiveDays": 0
}
```

Include an `ETag` response header (for example, `"retention-v1"`) and have `PUT` require that value in `If-Match`, validate the 1–3650 bounds for log/execution/audit days, allow archive days from 0–3650 (positive when archiving is enabled), persist the body in the mock, and return the new ETag. Use synthetic data only. This mock exercises the settings form, not SQL cleanup or archive durability. Remove it from any deployed build; the real API remains authoritative.

## SQL Server installation and migration

Use SQL Server 2022 or newer with the database compatibility level required by the WorkerDock installation (160 by default). Back up the tenant database and verify the backup before applying the numbered migration. Task 19 adds tenant migration `0008_retention`; use the repository's `WorkerDock.DatabaseMigrator` and the paired `0008_retention.up.sql` script rather than applying ad hoc edits. The migrator verifies the recorded checksum and schema history, checks prerequisites and transactional boundaries, and records the applied migration. Preserve the migration manifest with the release.

Use a separate migration identity for schema changes. The IIS, scheduler and worker runtime identities must not have schema-change rights. Runtime retention access is limited to the application's required data operations and the restricted purge routine. Ordinary service principals cannot directly delete audit events. Inspect the migration's documented prerequisites and current schema history before applying it to an existing tenant.

For rollback, stop or disable the retention maintenance job first, preserve the archive and database backup, then use the paired `0008_retention.down.sql` through the migrator. The down migration refuses unsafe data loss; follow its reported restore prerequisites. Do not hand-delete migration history or edit an applied migration. If a destructive rollback condition is present, restore or follow the forward repair path rather than bypassing the guard.

## Archive location and deployment ACL

Archiving is opt-in per tenant policy. Set `Retention:ArchiveRoot` (environment variable `Retention__ArchiveRoot`) to the same protected root in the tenant web and scheduler configurations; never accept a path from the browser, tenant API or archive request. Keep the root outside the web content tree and on storage with capacity monitoring and backups appropriate to the records. Grant only the scheduler service identity the minimum rights needed to create and finalize archives, verify/read them, and remove expired archives. The web and worker identities do not need filesystem access. Administrators should retain management rights for recovery. Confirm the effective ACL using the actual scheduler service identity before enabling archive-before-delete.

The writer must canonicalize paths beneath the configured root, reject root escapes and reparse-point targets, and write bounded compressed safe records to a temporary batch file. It flushes and finalizes the file, computes SHA-256 and row count, then publishes the archive and manifest atomically. A manifest records a stable batch ID, relative path, digest, row count and finalization time. A retry uses the same batch identity and must not create a second logical archive. When an archive file expires, WorkerDock removes its per-record SQL mapping; the compact batch manifest and purge summaries remain through the configured audit window, then are removed. Archive-file and metadata expiry are retried safely if the filesystem or database is unavailable.

## Integrity, deletion and failure behavior

Retention processes bounded batches. It first aggregates data needed by operational summaries, selects only eligible records, writes the archive when enabled, and reads back/verifies the finalized archive against its manifest before deleting the corresponding SQL rows. Deletion is limited to rows whose archive identity and integrity check succeeded. Audit cleanup uses the restricted purge routine and emits a purge-summary audit record.

If the disk fills, the ACL denies access, a write is interrupted, finalization fails or the checksum/read-back is invalid, the batch is reported as failed and its source rows remain. Retry with the same logical batch after resolving the storage or permission issue. Do not manually remove source rows or partially written files to make the run appear successful; follow the operator recovery procedure and retain evidence for investigation.

Archives contain safe exported records and integrity metadata. They are not SQL backups, point-in-time recovery, or a substitute for the database backup and restore plan. A valid SHA-256 digest detects accidental change relative to the manifest; by itself it does not prove who created the archive or protect against an administrator changing both data and manifest. Protect the archive and its manifest with the same storage controls and backup discipline.

## Eligibility and replay safeguards

A short retention setting never overrides live business dependencies. Keep active executions and attempts, current/referenced revisions, unresolved outbox or publication intents, open incidents and other records needed to resolve dependencies. Keep compact execution and delivery deduplication state until associated Quartz work is confirmed retired and the replay window has closed. Manual request keys remain available for the full 24-hour retry window after acceptance even if execution history retention is shorter. Retention must not infer that a job is retired merely because a Quartz trigger is absent; remove Quartz jobs, triggers and calendars through `IScheduler`, then confirm retirement before purging related state. Never edit Quartz tables directly.

WorkerDock does not claim exactly-once external effects. A durable archive or deletion does not make a handler's external side effects replay-safe; retries and recovery continue to follow the execution idempotency and uncertain-outcome rules. When in doubt, preserve source data and investigate before retrying cleanup.

## Limits and operations

The scheduler runs maintenance with bounded batch size and execution time. Archive capacity, ACL correctness, SQL availability and retention-job health remain operational dependencies. A prolonged outage delays cleanup; it does not authorize deleting unarchived rows. Monitor retention failures, archive volume, free space, finalization/verification errors and the age of the oldest eligible record. Alerting itself may be unavailable during a full scheduler/database outage, so check service and host health independently.

Retention settings do not shorten required audit, replay or active-work safeguards. Archive retention controls finalized archive batches independently; it does not change SQL history eligibility. Database administrators can still alter data, so application audit is append-only by permission design rather than an immutable external ledger.
