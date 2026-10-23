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
