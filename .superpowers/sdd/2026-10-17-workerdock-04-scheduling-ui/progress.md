# Task 14 ledger

- [x] Added bounded Skip/Coalesce/CatchUp policy and policy tests.
- [x] Added schedule and lifecycle contracts, SQL persistence, occurrence materialization, outbox-driven Quartz projection, HTTP endpoints, and paired migration.
- [x] Routed Task 13 `EnableManual` through the shared lifecycle implementation with a schedule-free guard.
- [x] `dotnet build WorkerDock.slnx` passed with 0 warnings and 0 errors.
- [x] Unit tests passed 33/33, including delayed-current-revision and stale/removed projection rereads.
- [x] Policy tests passed 5/5; all SQL migration up/down checksums passed 3/3 migration-input tests.
- [x] Added SQL integration scenarios for competing occurrence dispatchers and injected timezone-rule changes, plus disable and pause/resume cases.
- [x] Added bounded timezone-rule drift scanning that revises future projection through the outbox without rewriting accepted work.
- [x] Added SQL scenarios for manual run during pause, concurrent ETag edits, profile revision pinning, archive with running work, and held-delivery release on enable.
- [x] Delayed-initial and stale-removed Quartz projection cases pass without SQL.
- [ ] SQL-backed lifecycle/recovery scenarios and real migration apply/rollback: `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset.
- [x] Review follow-up regressions added for rowversion pause/resume, paused edit preservation, persisted no-op Enable, stale occurrence horizon, >100 timezone schedules, and per-occurrence recovery; policy RED confirmed before fix.
- [x] Release build passed with 0 warnings/errors; unit tests passed 33/33; 6 policy and 3 migration checksum tests passed; `git diff --check` passed.
- [ ] Thirteen SQL lifecycle tests and live migration apply/rollback remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset; commit pending.
- [x] Persisted CatchUp horizon/budget now spans 100-occurrence pages; overflow/age skips are recorded, with a 150-occurrence pure regression and SQL persistence regression added.
- [x] Follow-up verification: Release solution build 0 warnings/errors; unit 33/33; policy 7/7; migration checksum tests 3/3; `git diff --check` passed.
- [ ] Fourteen SQL lifecycle assertions (including cross-page persisted budget) remain `SQL_TEST_BLOCKED`; second review-fix commit pending.
