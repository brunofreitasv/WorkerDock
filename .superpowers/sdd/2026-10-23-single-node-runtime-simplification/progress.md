# Single Node Runtime Simplification Progress

Plan: `docs/superpowers/plans/2026-10-23-single-node-runtime-simplification.md`
Worktree: `C:\Users\bruno\.codex\worktrees\template-max-properties\WorkerDock 2`
Plan base: `3e7b103d85d3bb5ecc7075826534a7fe8624352f`

## Preflight

| Task pair / task | Shared file or interface | Ruling |
|---|---|---|
| 1 / 4 | `IScheduleEvaluator`, schedule evaluator and occurrence tests | Task 1 establishes grammar, day-of-month AND day-of-week semantics, DST behavior and preview parity; Task 4 consumes that contract. |
| 1 / 7 | `src/WorkerDock.Web/Program.cs` | Apply Task 1 evaluator registration first; Task 7 then changes runtime and endpoint registrations. Preserve the pre-existing template and settings edits. |
| 2 / 3 | `IAttemptStore`, `SqlAttemptStore`, attempt tests | Task 2 defines direct claim and active-hold semantics; Task 3 adds child supervision against that interface. |
| 2 / 9 | SQL attempt adapter and Quartz fixtures | Keep compatibility adapter and schema until Task 9 deletes Quartz and old fixtures. |
| 3 / 8 | `SqlAttemptStore` | Task 3 owns lifecycle and lease correctness; Task 8 subsequently moves its status-event writes to `OperationalEvents`. |
| 4 / 7 | `SqlJobLifecycle` | Task 4 first removes schedule projection writes; Task 7 later consolidates settings and runtime state. |
| 4 / 8 | `OccurrenceWriter` | Task 4 owns cursor/materialization invariants; Task 8 later consolidates its event writes. |
| 5 / 6 | Alert delivery runner/store composition | Task 5 extracts direct durable delivery; Task 6 composes its loops in the tenant service. |
| 5 / 8 | `SqlAlertDeliveryStore`, alert tests | Task 5 owns lease and retry semantics; Task 8 later consolidates alert cursor/event storage. |
| 6 / 7 | Runtime status consumed by health/monitoring | Task 6 provides one runtime heartbeat; Task 7 adapts endpoints and UI to it. |
| 6 / 9 | Worker project and program | Task 6 adds the service; Task 9 removes the scheduler project and Quartz references. |
| 7 / 8 | Settings, transition schema and monitoring state | Task 7 owns job/runtime settings; Task 8 adds the operational timeline and retention layout. |
| 7 / 9 | Transition schema, manifest and snapshot | Keep additive changes in transition migration until Task 9 produces the fresh baseline. |
| 8 / 9 | Schema and alert/history classes | Task 8 validates consolidated semantics; Task 9 then squashes schema and deletes obsolete tables. |
| 7 / 10 | Worker monitoring UI and tests | Task 7 changes runtime labels/editing; Task 10 documents and tests final operator behavior. |
| 9 / 10 | Schema, fixtures and docs | Task 9 establishes final schema; Task 10 documents and verifies it. |
| 1 | Evaluator contract and search bounds | Implement restricted five-field cron; DOM and DOW are ANDed; nonexistent local times are skipped; ambiguous local times use the earlier UTC instant; output is strictly UTC ordered; searches remain bounded with safe error codes. |
| 2 | Direct admission while preserving business behavior | A ready-ID scan is advisory only. Under the tenant business transaction lock, re-read execution/job/revision and enforce enabled/archive/stop/template availability, tenant/template/job concurrent ceilings, overlap Queue/Skip/Allow, backlog and priority. Execution rows and active attempt states replace projection/outbox/permit rows as durable work and slot holds. Local semaphores enforce process child ceilings. Preserve manual idempotency and scheduled pinned revisions. |
| 3 | Child restart behavior | Reuse existing ownership and lease fencing; child-entry uncertainty becomes `NeedsReview`, and replay requires the existing explicit bounded policy. |
| 4 | Schedule source of truth | Schedule rows and cursor are authoritative; materialization is idempotent and pins the current approved job/profile revision in the same transaction. |
| 5 | Alert delivery source of truth | Durable alert-delivery rows own stable delivery IDs, leases, retry state and completion; no Quartz trigger is involved. |
| 6 | One runtime instance | One tenant-scoped named mutex, one heartbeat, and bounded independent loops; SQL/configuration failures stop admission. |
| 7 | Runtime/settings ownership | Keep safe receipts and API behavior; move limits to tenant/template/job settings and report one runtime in monitoring. |
| 8 | Operational data retention | One typed operational event timeline, focused alert delivery table, preserved audit/archive/tombstone integrity and active work. |
| 9 | Fresh-install schema | No deployed tenants means replace migration history with one guarded fresh-install baseline. Keep checksum, tenant identity and least-privilege validation. |
| 10 | Documentation and verification | Update all active operator/help docs; environment-dependent SQL/Windows gates must be marked unverified if unavailable. |

## Existing worktree edits to preserve

There are pre-existing uncommitted changes across application, tests, docs, frontend and database files. Task commits must stage only task-owned paths and preserve those edits. Known overlapping files include `src/WorkerDock.Web/Program.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobCommands.cs`, `src/WorkerDock.Infrastructure/Templates/SqlTemplateCatalog.cs`, worker/scheduler `Program.cs`, and `tests/WorkerDock.IntegrationTests/AttemptSqlTests.cs`.

## Task status

| Task | Status | Commit |
|---|---|---|
| 1 | complete; review approved | `d84024a` |
| 2 | complete; fresh review approved; SQL execution unverified without `WORKERDOCK_TEST_SQL` | `3418723`, `c85b185`, `33ea2b2` |
| 3 | complete; fresh review approved; SQL execution unverified without `WORKERDOCK_TEST_SQL` | `2c023fb`, `d20f663` |
| 4 | complete; Task 4 review fixes committed as `924d66a`; build passed; SQL behavior unverified without `WORKERDOCK_TEST_SQL` | `3f52a20`, `924d66a`
| 5 | complete; runner intentionally awaits Task 6 production caller; full build passed; access tests passed; SQL delivery unverified without `WORKERDOCK_TEST_SQL` | `ecebff8` |
| 6 | complete; fresh review approved; Worker build and focused process tests passed; SQL health remains unverified without `WORKERDOCK_TEST_SQL` | `20e4b77` |
| 7 | pending | |
| 8 | pending | |
| 9 | pending | |
| 10 | pending | |
