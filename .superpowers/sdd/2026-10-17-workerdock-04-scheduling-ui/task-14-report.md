# Task 14 implementation report

Implemented schedule persistence and preview endpoints, bounded missed-work policy, SQL occurrence cursor/materialization, schedule projection through the common outbox reconciler, Quartz trigger/calendar projection, lifecycle commands and endpoints, Task 13 manual-enable delegation, paired tenant migration, and scheduling documentation.

Occurrence materialization is serialized per schedule and rechecks the current schedule revision plus job enabled/archive state under SQL locks before inserting executions. It pins the accepted job revision and current connection-profile revision and delegates capacity/admission to the existing execution admission path. Recovery ranges are recorded; exact missed counts are left unknown for bounded scans. Schedule deletes are tombstones and do not delete accepted executions. Delivery remains at least once; no exactly-once claim is made.

Verification:

- `dotnet build WorkerDock.slnx`: PASS, 0 warnings and 0 errors after timezone reconciliation and regression tests were added.
- `dotnet test tests/WorkerDock.UnitTests --no-restore`: PASS, 33/33.
- `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~OccurrencePolicyTests|FullyQualifiedName~JobLifecycleTests" --no-restore`: 5 passed, 9 blocked before assertions by `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.`
- `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~MigrationInputTests" --no-restore`: PASS, 3/3, including all tenant migration up/down checksum pairs.
- Real SQL migration apply/rollback, competing-dispatcher, zone-rule change, stale-trigger, and lifecycle assertions: `SQL_TEST_BLOCKED`; no disposable SQL Server was configured.
- `dotnet build WorkerDock.slnx -c Release`: PASS, 0 warnings and 0 errors.
- `git diff --check`: PASS; Git emitted only the repository's configured line-ending normalization warnings.

The implementation injects the deployed timezone-rule fingerprint provider and scans at most 100 schedules per maintenance pass. Changed fingerprints advance the schedule revision and job ETag, add an audit record, and enqueue future projection. Existing accepted occurrences remain unchanged. A SQL-independent stale/removed projection test confirms stale events reread business state before Quartz calls.

The SQL suite includes competing dispatchers, pause/resume cursor reset, manual run during pause, disable against stale triggers, ETag edit races, current profile-revision pinning, archive with a running execution, held-delivery release on enable, and injected timezone-rule drift. The SQL-independent Quartz tests cover both a delayed initial projection (old event, latest business revision) and a stale projection against removed state. Because the SQL fixture is unavailable, these SQL cases compile but their assertions have not been executed. The plan's retry replay behavior remains enforced by the existing execution retry path; no separate Task 14 SQL retry-policy assertion was run.

The primary checkout was not modified. The unrelated pre-existing edit to `.superpowers/sdd/2026-10-17-workerdock-03-templates/progress.md` was preserved.

## Review follow-up (09e01b2)

Implemented the six Important review fixes: pause/resume now transactionally bumps the job rowversion; recovery classifies each occurrence against the tolerance and bounds CatchUp with timely work; timezone reconciliation stores a durable singleton continuation and rotates through pages of 100; schedule edits preserve Paused/PausedAtUtc; repeated Enable/EnableManual returns a persisted command receipt; and old occurrence horizons are ignored before cursor advancement. The 0005 up/down migration pair and checksums include the scan state table.

Added focused regressions for the recovery boundary (pure unit), pause/resume ETag changes, schedule save while paused, repeated Enable receipt lookup, stale horizon monotonicity, and timezone reconciliation beyond 100 schedules. The occurrence-policy test had a verified RED result before its fix: Skip returned all five candidates where only the timely 12:05 occurrence was expected. Post-fix verification: `dotnet build WorkerDock.slnx -c Release --no-restore -nr:false -p:UseSharedCompilation=false` passed with 0 warnings/errors; unit tests passed 33/33; the focused integration command passed the six policy tests and three migration checksum tests, while 13 SQL-backed lifecycle tests stopped at fixture construction with `SQL_TEST_BLOCKED: configure WORKERDOCK_TEST_SQL for a disposable SQL Server 2022+ test instance.` `git diff --check` passed with only line-ending normalization warnings. SQL assertions, including the newly added lifecycle, horizon, and >100 schedule regressions, remain unexecuted and are not reported as passed.

## CatchUp page-budget follow-up

Review found that a configured CatchUp limit could reset on each 100-candidate page. The schedule now stores a pinned recovery horizon and cumulative CatchUp executions; page materialization locks this state and atomically commits it with the cursor, executions, and skipped-occurrence recovery record. The pinned horizon is scanned before a later horizon is considered, and state clears with the final cursor advance. Transaction rollback leaves the prior budget unchanged. `ScheduleRecoveryEvents.SkippedOccurrences` records age-expired and over-budget omissions. A SQL-independent two-page test covers 150 old occurrences with a count of three; the SQL integration regression checks persistence across two writer calls. Verification for this follow-up: Release solution build passed with 0 warnings/errors, unit tests 33/33, pure policy tests 7/7, migration checksum tests 3/3, and `git diff --check` passed. Fourteen SQL lifecycle assertions, including persisted cross-page budget, remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset. The paired 0005 migration, rollback, checksum manifest, and scheduling docs include the new state and skipped count.
