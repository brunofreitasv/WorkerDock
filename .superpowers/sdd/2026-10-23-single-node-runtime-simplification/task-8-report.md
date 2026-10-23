# Task 8 implementer report

Status: DONE_WITH_CONCERNS — operational status and schedule recovery now share a typed timeline; retention policy and alert evaluation cursor moved to their owning rows. SQL behavior needs a configured SQL Server test instance.

## Implementation

- Added migration 0013 with a typed `OperationalEvents` table and backfill from execution, attempt-status and schedule-recovery history. Execution timeline and schedule-recovery alert readers now query this table; writers append to it.
- Moved the alert evaluation cursor to `AlertRules`. Exposed rule versioning continues to use `CurrentRevision`, so cursor writes do not invalidate a user's rule edit.
- Added retention settings to `TenantSettings`; the active retention service reads them there. It records purge summaries in `AuditEvents`, archives consolidated events, and uses a bounded operational-event purge procedure. A transitional mirror into old `RetentionPolicy` and `RetentionPurgeAudit` remains so the existing archive expiry procedure stays compatible until Task 9 replaces the baseline.
- Kept archive batches, archive items, audit events, and request tombstones. Current-state SQL includes migration 0013.
- Added retention preservation and schema guard tests; updated SQL fixtures to apply the latest schema.

## Verification

- `dotnet build WorkerDock.slnx --no-restore`: passed, 0 warnings and 0 errors.
- Static guards for migration checksums, typed events, and preserved archive state: 3 passed.
- SQL behavior filters were run once. SQL-dependent tests failed at fixture setup with `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` No SQL behavior result is claimed.
- Migration 0013 checksums match the manifest: up `E543B51B622F101E47AB13058A9DFA5ED0C56CD357D7491B79C3650007A902C4`; down `197A49D3307F8F78F801BBEDC946E133D01F92B376719C42F3AB8C1CA0EDAD2E`.

## Deferred to Task 9

- Drop legacy event, retention policy, purge-audit, and schedule-recovery objects after the fresh-install baseline and compatibility readers are replaced.
- Remove transitional retention mirrors and legacy retention-procedure dependencies.

## Review fix: durable archive purge coverage

- Added `ArchiveBatches.PurgedRowCount` as durable progress. Migration 0013 backfills it from `RetentionPurgeAudit`, whose rows are held until archive metadata expires; new purges update the batch count in the same transaction as deletion. Pending-batch selection and archive expiry now read this column, so audit-event retention cannot erase archive coverage.
- Added a regression test that purges old audit events before archive expiry and asserts the batch metadata still expires and a purge audit event is written. Added assertions for stable timeline order/cursor paging and secret-field rejection, execution aggregates, and alert cursor/revision behavior. Existing archive-writer tests verify SHA-256 hashes, repeat-write integrity, and tamper detection; existing archive persistence coverage checks secret exclusion.
- `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~RetentionSqlTests.Transition_schema_consolidates_operational_state_and_keeps_archive_integrity_records|FullyQualifiedName~RetentionSqlTests.Archive_progress_uses_batch_state_that_survives_audit_purge|FullyQualifiedName~ObservabilityTests.Execution_timeline_reads_only_the_typed_operational_events_table|FullyQualifiedName~CustomSqlSchemaTests.Every_paired_migration_targets_custom_schema_and_matches_exact_manifest_bytes|FullyQualifiedName~CustomSqlSchemaTests.Operational_retention_transition_consolidates_event_and_policy_state'`: passed, 5/5.
- `dotnet test tests/WorkerDock.UnitTests/WorkerDock.UnitTests.csproj --no-build --filter 'FullyQualifiedName~ArchiveWriterTests'`: passed, 9/9.
- Focused SQL behavior filters for archive purge progress/timeline/aggregates and alert cursor/revision: blocked before test execution, 4 cases failed with `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` The separate archive secret-exclusion integration case is blocked by the same missing environment variable.
- `dotnet build WorkerDock.slnx --no-restore` passed earlier in the implementation turn with 0 warnings and 0 errors. A follow-up build attempt after this SQL-only migration edit could not write generated `obj/bin` files (`UnauthorizedAccessException: Access denied`); the existing compiled test assemblies ran the checks above.
- Updated 0013 checksums: up `DFA3A0366AD1627EB4D3A01D7949F8CD8CE3B278954DCEA67A889B1F40E2475C`; down `3FC767CE18DAA735C8BE6FC60BC4ADBFF593AECC79C71977A6683EA28245B7C9`.
