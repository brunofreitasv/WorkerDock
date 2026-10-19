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

Final Task 14 closeout (supersedes earlier pending entries): commits `09e01b2`, `5fe4f4f`, `83650f4`; independent scoped re-review found no remaining Critical/Important issues. Final checks: Release build 0 warnings/errors, UnitTests 33/33, OccurrencePolicyTests 7/7, MigrationInputTests/checksums 3/3, diff check passed. Fourteen SQL lifecycle/recovery assertions and real SQL migration apply/rollback remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset. Task14 is complete with this explicit SQL-runtime limitation.

## Task 15 — Guided creation and job management

- [x] Added the six-stage create/review/activation flow, schedule editor and server-authoritative preview, policy controls, and en/pt-BR strings.
- [x] Added job details and pause/resume/disable/enable/archive/duplicate/schedule-delete/stop controls; duplicate loads as a disabled review draft.
- [x] Added E2E scenarios for locale and repeated-hour preservation, stale ETag draft retention, failed schedule save identity, idempotent run retry, lifecycle controls, viewer access, confirmed force stop, and narrow/desktop keyboard navigation.
- [x] Added the Read-authorized `/api/time-zones` endpoint.
- [x] Frontend build passed; frontend unit tests 13/13 passed; Playwright discovery passed with 4 scenarios; WorkerDock.Web build passed with 0 warnings/errors; `git diff --check` passed.
- [ ] Browser execution remains `PLAYWRIGHT_BLOCKED`: configured Chromium executable is absent. No browser RED could be observed. SQL-backed lifecycle/live migration checks remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset.
- [x] Task 15 implementation completed at base `83650f4`; commit `b1d99da` (`feat: add guided scheduling and job management workflows`).
- [x] Important review follow-up: stale ETag actions require explicit current-revision load, draft recovery, and fresh review; schedule-off save deletes with If-Match and confirms 404 before manual status/enable; wizard locale switch preserves draft/time values. Frontend build passed, unit tests 14/14, discovery 4 tests, diff check passed; browser execution `PLAYWRIGHT_BLOCKED` (missing Chromium). Commit `88d1e93` (`fix: safely resolve scheduling UI review findings`).
- [x] Important review follow-up 2: unresolved schedule deletion now retains truthful `unknown` state, blocks manual Enable, and leaves Pause/Disable available; duplicate derives state from the target GET. Added unit regressions for unconfirmed removal and scheduled-source duplication, plus E2E scenarios for DELETE failure, GET failure, and duplicate 404. Frontend tests 16/16, build passed, Playwright discovery 7 tests/2 files, diff check passed; browser `PLAYWRIGHT_BLOCKED` (missing Chromium); SQL runtime remains `SQL_TEST_BLOCKED` (`WORKERDOCK_TEST_SQL` unset). Commit `c3d026e` (`fix: preserve uncertain schedule state in job UI`).
- [x] Important review follow-up 3: scheduled Enable is allowed only for a selected confirmed schedule or confirmed manual state; unresolved/off-but-present states remain blocked. Safety Pause/Disable are shown for uncertain/removal-pending schedules. Added unit tests for scheduled activation and confirmed deletion before manual enable. Focused wizard tests 5/5, frontend unit tests 18/18, build and Playwright discovery pass (7 E2E tests); diff check passed; browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium). Commit `596f8c5` (`fix: allow enabling confirmed scheduled jobs`).
- [x] Important review follow-up 4: schedule PUT state remains `unknown` until a successful schedule GET; 412 and 503 remain unresolved, and an initial-save 412 requires explicit current-revision load/recovery/review. Added unit regressions for PUT 412, PUT 503, and confirmed success; updated the primary E2E to re-review after initial 412. Focused wizard tests 7/7, frontend unit tests 20/20, build and Playwright discovery pass (7 E2E tests); diff check passed; browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium). Commit `c492d93` (`fix: confirm schedule saves before activation`).
- [x] Important review follow-up 5: restoring a local schedule after loading a different persisted revision marks the schedule dirty; Enable stays blocked until a successful PUT and a matching schedule GET. Added the differing-current-cron regression. Focused wizard tests 8/8, frontend unit tests 21/21, build and Playwright discovery pass (7 E2E tests); diff check passed; browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium). Commit `ed2ad13` (`fix: mark restored schedules unconfirmed`).
- [x] Important review follow-up 6: semantic offset comparison accepts equivalent local and server TimeSpan forms (`-05:00`/`-05:00:00`), including positive and zero offsets, while malformed non-null offsets remain unconfirmed. Added the restored Once save-and-enable regression and direct positive/negative/zero comparison coverage. Focused wizard tests 12/12, frontend unit tests 25/25, build and Playwright discovery pass (7 E2E tests); browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium); SQL runtime remains `SQL_TEST_BLOCKED` (`WORKERDOCK_TEST_SQL` unset). Commit `fix: compare semantic schedule offsets`.
- [x] Important review follow-up 7: post-PUT confirmation accepts a null Once request offset only when the exact same local time/zone is read back with a parseable resolved offset. Conflict revision comparison remains strict; null/null and malformed offsets stay unconfirmed. Added a New York winter round-trip test plus null, malformed, and mismatched-local-time rejection checks. Focused wizard tests 14/14, frontend unit tests 27/27, build and Playwright discovery pass (7 E2E tests); browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium); SQL runtime remains `SQL_TEST_BLOCKED` (`WORKERDOCK_TEST_SQL` unset). Commit `fix: confirm resolved once schedule offsets`.

## Task 16 — Operator monitoring

- [x] Added safe bounded SQL projections, read-authorized dashboard/jobs/execution/workers/audit APIs, health readiness, and en/pt-BR operator views.
- [x] Added visible-only polling, hidden-tab pause, failure backoff/stale indicators, access/query tests, frontend unit polling tests, and monitoring E2E scenarios.
- [x] Documented monitoring and health credential configuration in `docs/monitoring.md`; added `WorkersAsync` as required for worker capability view.
- [x] Targeted server access/monitoring tests 32/32, frontend unit tests 30/30, frontend build, Playwright discovery (16 tests), and diff check passed. Follow-up unmount/deferred-request hook regression first reproduced a lingering timer, then passed with the active-effect scheduling guard (focused hook tests 3/3).
- [ ] Browser execution is `PLAYWRIGHT_BLOCKED`: Chromium executable missing at the configured Playwright path. SQL runtime is `SQL_TEST_BLOCKED`: `WORKERDOCK_TEST_SQL` is unset. Production host must register its managed `ISecretVault` provider.
