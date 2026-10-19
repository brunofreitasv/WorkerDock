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
