# Single-Node Runtime Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Quartz with one tenant runtime service and a small SQL-backed work model while retaining jobs, schedules, alerts, monitoring, audit, and retention.

**Architecture:** The web app commits business state to SQL Server; one Windows runtime service polls due schedules, executions, and alert deliveries and launches bounded isolated execution children. SQL rows are the only durable work queue. A named mutex prevents two runtime services for one tenant on the single host.

**Tech Stack:** .NET 10, C#, SQL Server 2022+ (compatibility level 160), Windows services and Job Objects, React/TypeScript, Vitest and Playwright. Do not add a broker or a Quartz replacement package.

**Spec:** `docs/superpowers/specs/2026-10-23-single-node-runtime-simplification-design.md`

## Global Constraints

- One runtime service per tenant on one Windows machine; multiple child processes under one configurable ceiling.
- No Quartz package, namespace, project, runtime schema, or active documentation reference in the final tree.
- Preserve one-time and restricted five-field cron schedules, IANA time zones, explicit DST rules, pause/resume, and bounded missed-work policies.
- Preserve manual-run idempotency for 24 hours, pinned revisions, limits, stop, bounded retry, uncertain-outcome review, and at-least-once external-effect semantics.
- Keep alerts, monitoring, audit, archive-before-delete, retention, secret references, and the restricted SQL runtime role.
- There are no deployed tenant databases. Replace the draft migration chain with a fresh-install baseline; do not build an upgrade converter.
- The designated worktree has unrelated edits. Do not discard or commit those edits with a task; reconcile them with the new baseline.

## Review Focus

1. A sparse cron such as `0 0 1 1 *` and a DST transition must produce the same preview and materialized UTC occurrence; Task 1 and Task 4 tests cover both.
2. Starting a second tenant runtime must fail before it can claim work; Task 6 tests the mutex and ownership path.
3. A crash after a child may have started external work must not silently replay it; Task 3 tests `NeedsReview` and explicit replay policy.
4. A lost alert-delivery lease after a webhook call must not fabricate success or change the stable delivery ID; Task 5 tests it.
5. Retention must preserve live attempts, current deduplication windows, and archive integrity after consolidation; Task 8 tests all three.

---

## File map and task boundaries

- `src/WorkerDock.Infrastructure/Scheduling/FiveFieldScheduleEvaluator.cs`: all cron parsing, next-instant calculation, and local-once resolution; the existing `IScheduleEvaluator` stays the consumer contract.
- `src/WorkerDock.Application/Executions/IExecutionQueue.cs`, `src/WorkerDock.Infrastructure/Executions/SqlExecutionQueue.cs`: bounded ready-ID enumeration; `SqlAttemptStore` owns the atomic claim and attempt lifecycle.
- `src/WorkerDock.Application/Scheduling/IScheduleSweep.cs`, `src/WorkerDock.Infrastructure/Scheduling/SqlScheduleSweep.cs`: due schedule scan; `OccurrenceWriter` remains the transaction that advances cursor and inserts executions.
- `src/WorkerDock.Infrastructure/Alerts/AlertDeliveryRunner.cs`: delivery/renewal logic extracted from the Quartz job; `SqlAlertDeliveryStore` owns due enumeration and leases.
- `src/WorkerDock.Worker/TenantRuntimeService.cs`, `RuntimeLoops.cs`, and `RuntimeMutex.cs`: service composition, bounded loops, and single-instance guard. Keep `Program.cs` for dependency wiring only.
- `src/WorkerDock.Infrastructure/Runtime/SqlRuntimeStatus.cs`: single-row heartbeat/readiness and runtime limit acknowledgement; monitoring and health read it.
- Existing `SqlJobCommands`, `SqlJobLifecycle`, `SqlScheduleCommands`, `SqlMonitoringQueries`, `SqlRetentionService`, `IncidentEvaluator`, `SqlAlertRules`, and `SqlAlertDeliveryStore` change with their owning feature tasks; avoid a new generic repository layer.
- `database/tenant/0001_initial.up.sql`, `.down.sql`, `manifest.json`, and `current-state.sql`: final fresh-install schema. `MigrationRunner` continues to validate checksums, schema history, tenant identity, and least-privilege roles.
- `src/WorkerDock.Quartz`, `src/WorkerDock.Scheduler`, their obsolete tests/fixtures, and obsolete docs are deleted only when the replacement passes focused tests.

During Tasks 2–8, keep the old tables available so each new path can be tested before cutover. Put additive schema changes in a temporary `database/tenant/0011_single_node_transition.up.sql`/`.down.sql`, update its manifest checksums and `current-state.sql` with each schema-bearing task, and have SQL fixtures apply the latest target. Task 9 squashes the draft chain and transition into `0001_initial`. Never claim full-suite passage while an interim schema and code path are intentionally mismatched.

### Task 1: Quartz-free schedule evaluator

**Files:** Create `src/WorkerDock.Infrastructure/Scheduling/FiveFieldScheduleEvaluator.cs`; modify `src/WorkerDock.Web/Program.cs`, `src/WorkerDock.Infrastructure/WorkerDock.Infrastructure.csproj`, `tests/WorkerDock.UnitTests/ScheduleEvaluatorTests.cs`, `tests/WorkerDock.IntegrationTests/OccurrencePolicyTests.cs`.

**Interfaces:** Produce `public sealed class FiveFieldScheduleEvaluator : IScheduleEvaluator` with the existing `Validate(ScheduleDefinition)`, `Next(ScheduleDefinition, DateTimeOffset, int)`, and `ResolveLocalOnce(DateTime, string, TimeSpan?)` signatures. No Quartz type crosses this boundary.

- [ ] Write `Next_SparseYearlyCron_ReturnsJanuaryFirst`: `Assert.Equal(expectedJanuaryFirstUtc, evaluator.Next(schedule, afterUtc, 1).Single());`. Extend the same test file for values, lists, ranges, steps, AND day matching, impossible schedules, DST skip/earlier-offset behavior, and UTC ordering.
- [ ] Run `dotnet test tests/WorkerDock.UnitTests/WorkerDock.UnitTests.csproj --filter FullyQualifiedName~ScheduleEvaluatorTests`; expect a failing new test.
- [ ] Implement the restricted five-field parser and evaluator with bounded, field-aware advancement; switch web registration from `QuartzScheduleEvaluator`. Preserve existing safe error codes.
- [ ] Run the unit filter and `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~OccurrencePolicyTests`; expect pass.
- [ ] Commit only this task's files: `feat: evaluate schedules without Quartz`.

### Task 2: Direct SQL execution queue and capacity gate

**Files:** Create `src/WorkerDock.Application/Executions/IExecutionQueue.cs`, `src/WorkerDock.Infrastructure/Executions/SqlExecutionQueue.cs`; modify `src/WorkerDock.Application/Executions/IAttemptStore.cs`, `src/WorkerDock.Infrastructure/Executions/SqlAttemptStore.cs`, `src/WorkerDock.Infrastructure/Executions/InMemoryAttemptStore.cs`, `tests/WorkerDock.IntegrationTests/AttemptSqlTests.cs`, `tests/WorkerDock.IntegrationTests/JobAdmissionTests.cs`. Reuse the existing `JobAdmission.Decide` policy function.

**Interfaces:** `IExecutionQueue.ReadReadyAsync(int limit, CancellationToken ct) : ValueTask<IReadOnlyList<Guid>>`; add `IAttemptStore.BeginAsync(Guid executionId, string workerBootId, CancellationToken ct) : ValueTask<AttemptStart>`. Keep the old `DispatchIntent` overload only until Task 9 so interim Quartz tests compile. The SQL transaction, not the candidate read, decides whether a row can start. The bounded ready query filters candidates clearly blocked by job capacity or Queue overlap to prevent head-of-line starvation, keeps Skip candidates available for skip settlement, preserves Allow sharing, and treats a due RetryWaiting execution as retaining its template slot.

- [ ] Add `ConcurrentClaims_StartOneAttempt`: `Assert.Single(await fixture.ReadAttemptsAsync(executionId));`. Cover disabled/archived jobs, stopped executions, unavailable templates, full template/job limits, future `AvailableAtUtc`, and a `NeedsReview` job-slot hold. Also cover a due retry at template limit one, Skip and Allow behavior, and a ready batch that reaches runnable work behind full/queued jobs. The single-host tenant child ceiling is enforced by Task 6's local semaphore; the tenant schema has no tenant concurrency limit table, so do not add one.
- [ ] Run `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter 'FullyQualifiedName~AttemptSqlTests|FullyQualifiedName~JobAdmissionTests'` with `WORKERDOCK_TEST_SQL`; expect the new direct-claim tests to fail.
- [ ] Implement the bounded ready query and short transaction: lock/recheck current execution and job, count active holds, insert attempt, set running state, and return the existing `AttemptLease`. Make queued manual and scheduled rows the source of dispatch; no outbox publication is needed for this path.
- [ ] Run the same SQL tests; expect pass. Keep old Quartz adapters compiling until Task 9.
- [ ] Commit: `feat: claim executions directly from SQL`.

### Task 3: Child supervision, restart classification, and retry

**Files:** Create `src/WorkerDock.Runtime/Workers/ExecutionDispatchLoop.cs`; modify `src/WorkerDock.Infrastructure/Executions/SqlAttemptStore.cs`, `src/WorkerDock.Runtime/Ownership/OwnershipMonitor.cs`, `tests/WorkerDock.ProcessTests/ExecutionControlTests.cs`, `tests/WorkerDock.IntegrationTests/AttemptLifecycleTests.cs`, `tests/WorkerDock.IntegrationTests/AttemptSqlTests.cs`.

**Interfaces:** `ExecutionDispatchLoop(IExecutionQueue queue, IAttemptStore attempts, IAttemptRunner runner, TimeProvider clock, int maxChildren).RunOnceAsync(CancellationToken ct) : ValueTask<int>`. Reuse `ExecutionSupervisor` and `AttemptLease`; no child-facing protocol change.

- [ ] Add `RestartAfterPossibleHandlerEntry_RequiresReview`: `Assert.Equal("NeedsReview", await fixture.ExecutionStateAsync(executionId));`. Cover concurrent children within `maxChildren`, pre-entry descriptor failure, future retry delay, stop/drain, and explicitly permitted bounded replay.
- [ ] Run process and SQL filters `FullyQualifiedName~ExecutionControlTests|FullyQualifiedName~AttemptLifecycleTests|FullyQualifiedName~AttemptSqlTests`; expect the new direct-dispatch tests to fail.
- [ ] Implement bounded dispatch using Task ownership, existing renewal/containment, and `ReviewExpiredAsync(100, ct)` before admitting new work. Keep exact completion fencing and stable execution idempotency key.
- [ ] Run the same filters; expect pass.
- [ ] Commit: `feat: supervise SQL-dispatched execution children`.

### Task 4: Direct schedule materialization

**Files:** Create `src/WorkerDock.Application/Scheduling/IScheduleSweep.cs`, `src/WorkerDock.Infrastructure/Scheduling/SqlScheduleSweep.cs`; modify `src/WorkerDock.Infrastructure/Scheduling/OccurrenceWriter.cs`, `src/WorkerDock.Infrastructure/Scheduling/SqlScheduleCommands.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobLifecycle.cs`, `tests/WorkerDock.IntegrationTests/OccurrencePolicyTests.cs`, `tests/WorkerDock.IntegrationTests/CalendarExecutionTests.cs`, `tests/WorkerDock.UnitTests/ScheduleEvaluatorTests.cs`.

**Interfaces:** `IScheduleSweep.RunDueAsync(DateTimeOffset throughUtc, int maxSchedules, CancellationToken ct) : ValueTask<int>` and `RefreshZoneRulesAsync(int maxSchedules, CancellationToken ct) : ValueTask<int>`; `OccurrenceWriter.MaterializeAsync(Guid scheduleId, long scheduleRevision, DateTimeOffset throughUtc, CancellationToken ct)` remains the only cursor/insert transaction.

- [ ] Add `RepeatedSweep_MaterializesOccurrenceOnce`: `Assert.Single(await fixture.ExecutionsForOccurrenceAsync(scheduleId, scheduledUtc));`. Cover enable without projection, pause/resume, stale revision, DST/sparse preview parity, changed time-zone rules advancing schedule revision, and cumulative catch-up budget.
- [ ] Run `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter 'FullyQualifiedName~OccurrencePolicyTests|FullyQualifiedName~CalendarExecutionTests'` with SQL; expect new tests to fail.
- [ ] Implement due and bounded time-zone-rule scans and remove schedule-projection writes from management transactions. Keep schedule cursor, occurrence uniqueness, pinned job/profile revision, and recovery policy in the same materialization transaction.
- [ ] Run the SQL filters; expect pass.
- [ ] Commit: `feat: materialize schedules directly from SQL`.

### Task 5: Direct alert evaluation and delivery

**Files:** Create `src/WorkerDock.Infrastructure/Alerts/AlertDeliveryRunner.cs`; modify `src/WorkerDock.Application/Alerts/IAlertDeliveryStore.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertDeliveryStore.cs`, `src/WorkerDock.Infrastructure/Alerts/IncidentEvaluator.cs`, `tests/WorkerDock.IntegrationTests/AlertDeliveryTests.cs`, `tests/WorkerDock.IntegrationTests/AlertAccessTests.cs`.

**Interfaces:** Add `DueAlertDelivery(Guid DeliveryId, int AttemptOrdinal)` and `IAlertDeliveryStore.ReadDueAsync(int limit, CancellationToken ct) : ValueTask<IReadOnlyList<DueAlertDelivery>>`; `AlertDeliveryRunner.RunAsync(DueAlertDelivery due, CancellationToken ct) : ValueTask` uses existing claim/renew/complete contracts.

- [ ] Add `LeaseLostAfterSend_DoesNotCompleteDelivery`: `Assert.NotEqual("Succeeded", await fixture.DeliveryStateAsync(deliveryId));`. Cover direct incident-to-delivery insertion, duplicate claims, retry due time, stable delivery ID, and pinned sink revision.
- [ ] Run `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AlertDeliveryTests` with SQL; expect new tests to fail.
- [ ] Move the Quartz delivery job's bounded send/lease logic into `AlertDeliveryRunner`; read due rows in batches and remove alert outbox publication.
- [ ] Run alert delivery and access filters; expect pass.
- [ ] Commit: `feat: deliver alerts from durable SQL rows`.

### Task 6: One tenant runtime service

**Files:** Create `src/WorkerDock.Worker/TenantRuntimeService.cs`, `src/WorkerDock.Worker/RuntimeLoops.cs`, `src/WorkerDock.Worker/RuntimeMutex.cs`, `src/WorkerDock.Infrastructure/Runtime/SqlRuntimeStatus.cs`, `database/tenant/0011_single_node_transition.up.sql`, `database/tenant/0011_single_node_transition.down.sql`; modify `src/WorkerDock.Worker/Program.cs`, `src/WorkerDock.Worker/WorkerDock.Worker.csproj`, `database/tenant/manifest.json`, `database/tenant/current-state.sql`, `tests/WorkerDock.ProcessTests/TenantSqlHostStartupTests.cs`, `tests/WorkerDock.IntegrationTests/TenantSqlWebStartupTests.cs`.

**Interfaces:** `RuntimeMutex.Acquire(Guid instanceId) : IDisposable`; `SqlRuntimeStatus.HeartbeatAsync(Guid bootId, bool ready, CancellationToken ct) : ValueTask`; `TenantRuntimeService : BackgroundService` composes Tasks 2–5 plus alert evaluation, metric aggregation, and retention. Enforce the configured tenant maximum child count with a local semaphore because this deployment runs one runtime service on one host and the tenant schema has no concurrency limit table. Use tenant-scoped `Global\\WorkerDock.<instanceId:N>` mutex held and released by the same dedicated thread for the service lifetime; fail closed if acquisition fails.

- [ ] Add `SecondRuntimeForTenant_IsRejected` as a two-process test: `Assert.Equal("RuntimeAlreadyRunning", secondProcess.SafeExitCode);` while the first service holds the mutex. Cover restart after release, lost-SQL readiness, bounded shutdown, the local child semaphore ceiling, and a web health check that reads the heartbeat.
- [ ] Run `dotnet test tests/WorkerDock.ProcessTests/WorkerDock.ProcessTests.csproj --filter FullyQualifiedName~TenantSqlHostStartupTests`; expect new tests to fail.
- [ ] Wire bounded independent loops and host-level child ceiling. Use one boot identity and heartbeat row. Keep the existing vault boundary and installed template digest checks. Stop admission on fatal configuration/schema mismatch.
- [ ] Run process and SQL startup filters; expect pass.
- [ ] Commit: `feat: host one tenant runtime service`.

### Task 7: Simplify job, settings, and monitoring state

**Files:** Modify `src/WorkerDock.Infrastructure/Jobs/SqlJobCommands.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobLifecycle.cs`, `src/WorkerDock.Infrastructure/Executions/SqlExecutionControls.cs`, `src/WorkerDock.Infrastructure/Executions/SqlCommandOutcomes.cs`, `src/WorkerDock.Infrastructure/Workers/SqlCapacityCommands.cs`, `src/WorkerDock.Infrastructure/Templates/SqlTemplateCatalog.cs`, `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`, `src/WorkerDock.Web/Program.cs`, `src/WorkerDock.Web/Endpoints/CapacityEndpoints.cs`, `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`, `src/WorkerDock.Web/Endpoints/HealthEndpoints.cs`, `frontend/apps/tenant/src/features/monitoring/WorkersPage.tsx`, the transition SQL/manifest/snapshot; test `tests/WorkerDock.IntegrationTests/CommandOutcomeTests.cs`, `tests/WorkerDock.IntegrationTests/CapacityPolicySqlTests.cs`, `tests/WorkerDock.IntegrationTests/MonitoringAccessTests.cs`, `tests/WorkerDock.IntegrationTests/TemplateCatalogSqlTests.cs`.

**Interfaces:** Preserve existing job/schedule/manual-run management routes and safe command receipts. Replace per-worker limit editing with tenant host ceiling and per-template/job limits; `/api/monitoring/workers` reports the single runtime status until the UI uses a clearer runtime label.

- [ ] Add `ManualRequestRetry_ReturnsOriginalExecution`: `Assert.Equal(first.ExecutionId, retry.ExecutionId);`. Cover 24-hour scope, pending/review slot holds, synchronous receipts, installed-template validation, one-runtime health, and removal of projection-lag status.
- [ ] Run SQL filters `FullyQualifiedName~CommandOutcomeTests|FullyQualifiedName~CapacityPolicySqlTests|FullyQualifiedName~MonitoringAccessTests`; expect new tests to fail.
- [ ] Move manual dedup fields/unique index to executions; fold limits and egress settings into owning rows; replace worker capability/policy/permit queries with runtime status and execution holds. Update the monitoring UI copy and remove the per-worker edit control. Task 8 owns retention-policy storage.
- [ ] Run those SQL filters and `npm test -- --run` from `frontend`; expect pass.
- [ ] Commit: `refactor: remove distributed worker state`.

### Task 8: Consolidate operational history, alerts, and retention

**Files:** Modify `src/WorkerDock.Infrastructure/Observability/SqlExecutionEvents.cs`, `src/WorkerDock.Infrastructure/Observability/SqlExecutionEventQueries.cs`, `src/WorkerDock.Infrastructure/Observability/MetricAggregation.cs`, `src/WorkerDock.Infrastructure/Scheduling/OccurrenceWriter.cs`, `src/WorkerDock.Infrastructure/Executions/SqlAttemptStore.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertRules.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertQueries.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertDeliveryStore.cs`, `src/WorkerDock.Infrastructure/Retention/SqlRetentionService.cs`, the transition SQL/manifest/snapshot, `tests/WorkerDock.IntegrationTests/ObservabilityTests.cs`, `tests/WorkerDock.IntegrationTests/AlertDeliveryTests.cs`, `tests/WorkerDock.IntegrationTests/TenantSnapshotTests.cs`; create `tests/WorkerDock.IntegrationTests/RetentionSqlTests.cs`.

**Interfaces:** One typed `OperationalEvents` table stores execution status events and schedule recovery events; alert-delivery attempts remain in their focused table. Public monitoring, alert, and retention DTOs keep their safe fields and pagination bounds.

- [ ] Add `RetentionBatch_PreservesActiveWorkAndDedupWindow`: `Assert.True(await fixture.ExecutionExistsAsync(activeId));` and `Assert.True(await fixture.TombstoneExistsAsync(requestKey));`. Cover timeline order, aggregates, alert revisions/cursor, archive hashes, purge audit, and absence of secret values in events/manifests.
- [ ] Run SQL filters `FullyQualifiedName~ObservabilityTests|FullyQualifiedName~AlertDeliveryTests|FullyQualifiedName~RetentionSqlTests`; expect new tests to fail.
- [ ] Move status/recovery events to the typed timeline, place alert cursor on the rule row, put retention policy on tenant settings, and use `AuditEvents` for purge decisions. Keep archive batch/item/tombstone records.
- [ ] Run the same filters; expect pass.
- [ ] Commit: `refactor: consolidate operational history and retention state`.

### Task 9: Replace draft SQL chain and delete Quartz

**Files:** Create `database/tenant/0001_initial.up.sql`, `.down.sql`; modify `database/tenant/manifest.json`, `current-state.sql`, `permissions.sql`, `permissions.rollback.sql`, `src/WorkerDock.Infrastructure/Migrations/MigrationRunner.cs`, solution/project references and lock files; delete superseded `database/tenant/0001_bootstrap*` through `0010_remove_builtin_v1*`, `database/upstream/quartz-4.5.1`, `src/WorkerDock.Quartz`, `src/WorkerDock.Scheduler`, Quartz-only fixtures/tests and `tests/WorkerDock.TestHost` if no remaining caller. Modify `tests/WorkerDock.IntegrationTests/CustomSqlSchemaTests.cs`, `MigrationTests.cs`, `MigrationInputTests.cs`, `TenantSnapshotTests.cs`, `tests/scripts/Invoke-TenantMigration.Tests.ps1`.

**Interfaces:** The manifest has one target, `0001_initial`; `MigrationRunner.ApplyAsync("tenant", "0001_initial", ct)` creates the complete schema, and rollback works only when its data-safety guard permits. `current-state.sql`, if retained, provisions the same objects for an empty database.

- [ ] Add `FreshInstall_HasNoQuartzObjects`: `Assert.DoesNotContain(objects, name => name.Contains("QRTZ", StringComparison.OrdinalIgnoreCase));`. Cover snapshot parity, the reviewed table inventory, manifest checksum, runtime permissions, tenant ID, and guarded rollback.
- [ ] Run migration/schema filters `FullyQualifiedName~CustomSqlSchemaTests|FullyQualifiedName~MigrationTests|FullyQualifiedName~TenantSnapshotTests` with SQL; expect the new inventory test to fail.
- [ ] Build the final focused schema and update each remaining SQL query. Remove obsolete projects, sources, fixtures, package references, lock entries, and generated assets. Do not remove a table until all its owning feature tests pass.
- [ ] Run schema filters, `dotnet build WorkerDock.slnx`, and `rg -n 'Quartz|QRTZ|WDS_|WDE_' src database/tenant tests --glob '!*.md'`; expect passing commands and no matches.
- [ ] Commit: `refactor: replace Quartz schema with fresh tenant baseline`.

### Task 10: Documentation and end-to-end verification

**Files:** Modify `README.md`, `docs/reference/architecture.md`, `scheduling.md`, `database-objects.md`, `database-migrations.md`, `execution-guarantees.md`, `monitoring.md`, `alerts.md`, `configuration.md`, `deployment-model.md`, `docs/how-to/setup-sql-server.md`, `deploy-production.md`, `configure-tenants.md`, `manage-jobs.md`, `manage-retention-and-archives.md`, `docs/tutorials/run-with-sql-server.md`, `docs/validation/release-checklist.md`; delete `docs/reference/quartz-integration.md`; update affected `frontend/apps/tenant/src/help/content/{en,pt-BR}/*.md`, relevant Vitest/Playwright tests and `tests/WorkerDock.ProcessTests`.

**Interfaces:** One documented Windows runtime service per tenant; SQL 2022+ fresh-install command targets `0001_initial`; configuration documents the concurrent-child ceiling and poll behavior. Keep the optional Control Center and development memory tutorial accurate.

- [ ] Add `single-runtime.spec.ts`: `expect(await page.getByText(/Quartz|projection/i).count()).toBe(0)` on the visible operator views. Cover concurrent children, uncertain restart, schedule recovery, alert retry, monitoring, and retention with process/SQL tests.
- [ ] Run the new process/SQL/frontend checks and confirm they fail against stale docs/UI assumptions or missing runtime behavior.
- [ ] Rewrite deployment, schema, operator, help, and validation docs; remove obsolete configuration and routes or map them to the new runtime state. Preserve the existing SQL secrets and outbound-policy instructions.
- [ ] Run `dotnet test WorkerDock.slnx`, `npm test -- --run`, `npm run build` from `frontend`, applicable Playwright tests, and real SQL process/integration checks with `WORKERDOCK_TEST_SQL`. Record unavailable environment-dependent gates as unverified, never as passed.
- [ ] Run `rg -n 'Quartz|QRTZ|WDS_|WDE_' README.md src database/tenant docs/reference docs/how-to docs/tutorials frontend --glob '!*.lock.json'`; expect no obsolete references. Review the final diff for unrelated worktree changes.
- [ ] Commit only task-owned files: `docs: describe single-node tenant runtime`.

## Final review

- [ ] Compare every acceptance item in the spec with passing test output or an explicit environment-dependent gap.
- [ ] Inspect `git status --short` and staged diff so pre-existing edits are not silently included in a commit.
- [ ] Request a whole-branch code review, fix actionable findings, rerun affected checks, and report any live SQL/Windows verification gap plainly.
