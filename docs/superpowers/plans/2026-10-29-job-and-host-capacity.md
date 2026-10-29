# Job and Host Capacity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make host and per-job running capacity the only numeric limits, expose job capacity in the UI, and make review-related Queue/Skip behavior explicit per job.

**Architecture:** A live job row holds `ConcurrencyLimit` and `PauseOnNeedsReview`; the database migration enables the new switch for existing jobs and defaults it off for new rows. Dispatch separates numeric running counts from review hold and queue order, with claim-time checks under the existing business transaction. The job UI uses one management command for both live settings; monitoring reports why a queued execution is waiting.

**Tech Stack:** .NET 10/C#, SQL Server tenant migrations, ASP.NET Core endpoints, React/TypeScript/MUI, xUnit/Vitest.

**Spec:** `docs/superpowers/specs/2026-10-29-job-and-host-capacity-design.md`

## Global Constraints

- Only Running attempts consume host or job numeric capacity; Queued, RetryWaiting, and NeedsReview consume none.
- No active template capacity gate or UI control; preserve template enable/retire checks.
- Existing jobs: review switch on. New jobs: review switch off. Job capacity defaults to 1.
- Queue is serial; Skip discards overlapping occurrences; Allow may run up to job and host capacity.
- Queue/Skip review hold applies only when the job switch is on; Allow ignores the switch.
- Preserve job queue order; blocked jobs never reserve host capacity or stall eligible jobs of other IDs.
- Preserve history, pinned execution details, Manage authorization, audit, and existing job-limit API.
- Do not resolve or retry abandoned executions automatically.

## Review Focus

- Migrated job versus new job: switch must be true for every pre-migration job and false for newly inserted jobs (Task 1).
- Legacy template limit of 0 or 1 with two jobs: both may start when host and job capacity allow (Task 2).
- Queued, RetryWaiting, and NeedsReview states: none consume numeric capacity; only opted-in review hold changes Queue/Skip (Task 2).
- Concurrent claims and reduced limits: never exceed host/job limits; already running attempts continue after a reduction (Task 2).
- Failed setting save during creation: draft stays disabled and a retry does not silently enable with unintended settings (Task 4).

---

### Task 1: Store and manage live job controls

**Files:**
- Create: `database/tenant/migrations/0005_job_review_hold.up.sql`, `database/tenant/migrations/0005_job_review_hold.down.sql`
- Modify: `database/tenant/manifest.json`, `database/tenant/current-state.sql`
- Modify: `src/WorkerDock.Application/Jobs/IJobCommands.cs`, `src/WorkerDock.Application/Workers/ICapacityCommands.cs`
- Modify: `src/WorkerDock.Infrastructure/Workers/SqlCapacityCommands.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobManagementQueries.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobCommands.cs`, `src/WorkerDock.Infrastructure/Jobs/InMemoryJobCommands.cs`, `src/WorkerDock.Infrastructure/Jobs/JobDbContext.cs`
- Modify: `src/WorkerDock.Web/Endpoints/CapacityEndpoints.cs`, `src/WorkerDock.Web/Program.cs`
- Test: `tests/WorkerDock.IntegrationTests/CapacityPolicySqlTests.cs`, `tests/WorkerDock.IntegrationTests/CapacityEndpointTests.cs`, `tests/WorkerDock.IntegrationTests/JobManagementQueryTests.cs`

**Interfaces:**
- Produces: `JobEditorDto(..., int ConcurrencyLimit, bool PauseOnNeedsReview)`; `ICapacityCommands.SetJobControlsAsync(Guid jobId, int limit, bool pauseOnNeedsReview, ActorContext actor, CancellationToken ct)`; `PUT /api/jobs/{id:guid}/execution-controls` with `{limit, pauseOnNeedsReview}`.
- Retains: `SetJobLimitAsync` and `PUT /api/jobs/{id:guid}/limit` for existing clients. Remove the template-limit setter from public routes and command interface; leave the old SQL column inert for compatibility.

- [ ] **Step 1: Write failing tests.** In `CapacityPolicySqlTests`, assert migration 0005 sets an existing job's `PauseOnNeedsReview=1`, a subsequently saved job gets `0`, and `SetJobControlsAsync(id,3,false,actor,ct)` changes both columns in one audited transaction. In `JobManagementQueryTests`, assert detail exposes both values. In `CapacityEndpointTests`, assert a viewer gets 403 and a request without CSRF gets 400 for the new route; the removed template route returns 404.
- [ ] **Step 2: Run focused tests and confirm the new assertions fail.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~CapacityPolicySqlTests|FullyQualifiedName~CapacityEndpointTests|FullyQualifiedName~JobManagementQueryTests"`
- [ ] **Step 3: Implement the schema, DTO, command, and route.** Add `Jobs.PauseOnNeedsReview bit NOT NULL DEFAULT(0)` in migration 0005, update existing rows to 1, grant runtime update, and update `current-state.sql` and SHA256 manifest/history from actual file hashes. Validate nonnegative limits, reject missing/archived jobs, audit both changed values, and trigger admission reevaluation. Keep definition edits from changing either live control.
- [ ] **Step 4: Run the focused tests and solution build.** Same test command; `dotnet build WorkerDock.slnx -c Release`. Expected: pass, with no migration checksum mismatch.
- [ ] **Step 5: Commit only Task 1 files.** `git add` the listed files and `git commit -m "feat: store and manage job execution controls"`.

### Task 2: Enforce running-only capacity and review scheduling

**Files:**
- Modify: `src/WorkerDock.Infrastructure/Dispatch/JobAdmission.cs`, `src/WorkerDock.Infrastructure/Executions/SqlExecutionQueue.cs`, `src/WorkerDock.Infrastructure/Executions/SqlAttemptStore.cs`, `src/WorkerDock.Infrastructure/Executions/AttemptLifecycle.cs`
- Modify: `tests/WorkerDock.IntegrationTests/SqlCapacityTestSetup.cs`
- Test: `tests/WorkerDock.IntegrationTests/JobAdmissionTests.cs`, `tests/WorkerDock.IntegrationTests/CapacityPolicySqlTests.cs`, `tests/WorkerDock.IntegrationTests/JobSqlIntegrationTests.cs`

**Interfaces:**
- Consumes: `Jobs.PauseOnNeedsReview` and `Jobs.ConcurrencyLimit` from Task 1.
- Produces: queue selection and claim behavior where the running attempt count is authoritative for host/job capacity; `NeedsReview` affects Queue/Skip only through the live switch. `SkippedReviewHold` is the lifecycle reason for Skip occurrences discarded because of review hold.

- [ ] **Step 1: Write failing admission and SQL tests.** Cover Queue and Skip with review switch on/off, Allow with switch on, a RetryWaiting execution alongside a later execution, two distinct jobs on the same template with legacy template limit 0/1 and host capacity 10, same-job Allow limit 2, a lower limit with existing runs, and two concurrent claims at the last available slot. Assert a blocked job does not prevent a second job from starting; assert review-held Skip records `SkippedReviewHold`.
- [ ] **Step 2: Run focused tests and confirm failure.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~JobAdmissionTests|FullyQualifiedName~CapacityPolicySqlTests|FullyQualifiedName~JobSqlIntegrationTests"`
- [ ] **Step 3: Update admission and both start paths.** Rework `JobAdmission.Decide` and `AdmitAsync` so nonrunning `HoldsJobSlot` rows do not reserve numeric capacity; preserve FIFO, Skip, and backlog behavior. In `SqlExecutionQueue.ReadReadyAsync` and `SqlAttemptStore.CanClaimAsync`, remove template-limit predicates and count only running attempts for job capacity; keep the host running-attempt check and make transactional recheck authoritative. Make both direct and intent-based `BeginAsync` use the same gates. Keep template enabled/retired checks. Release nonrunning numeric reservations on RetryWaiting/NeedsReview, while honoring the explicit review switch as a scheduling policy.
- [ ] **Step 4: Run focused tests and solution build.** Same test command; `dotnet build WorkerDock.slnx -c Release`. Expected: pass, no oversubscription assertions fail.
- [ ] **Step 5: Commit only Task 2 files.** `git add` the listed files and `git commit -m "fix: count only running attempts against capacity"`.

### Task 3: Explain queued and skipped execution state

**Files:**
- Modify: `src/WorkerDock.Application/Monitoring/MonitoringDtos.cs`, `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`
- Modify: `frontend/apps/tenant/src/features/monitoring/monitoringTypes.ts`, `frontend/apps/tenant/src/features/monitoring/ExecutionDetails.tsx`
- Modify: `frontend/packages/ui/locales/en.json`, `frontend/packages/ui/locales/pt-BR.json`
- Test: `tests/WorkerDock.IntegrationTests/MonitoringJobsSqlTests.cs`, `frontend/apps/tenant/src/features/monitoring/ExecutionDetails.test.tsx`

**Interfaces:**
- Consumes: scheduling rules and `SkippedReviewHold` from Task 2.
- Produces: nullable `ExecutionDetailsDto.WaitReason` and corresponding frontend field, with stable codes `ReviewHold`, `EarlierQueued`, `JobRunning`, `JobCapacity`, `HostCapacity`, `WorkerUnavailable`, `NotDue`; null when no current wait applies.

- [ ] **Step 1: Write failing monitoring/UI tests.** Assert a queued run reports `ReviewHold` for an opted-in review, `JobCapacity` for an Allow job at its running limit, `HostCapacity` at host ceiling, and no template-limit reason. Assert the UI renders the localized current reason and `SkippedReviewHold` on the timeline; avoid presenting a stored wait reason as historical fact.
- [ ] **Step 2: Run focused tests.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MonitoringJobsSqlTests"`; `npm --prefix frontend exec -- vitest run apps/tenant/src/features/monitoring/ExecutionDetails.test.tsx`. Expected: new assertions fail.
- [ ] **Step 3: Implement current wait-reason projection and UI labels.** Compute reasons from current database state in `SqlMonitoringQueries.ExecutionAsync`; show them only for Queued/RetryWaiting and label as current. Surface skip reason from existing lifecycle timeline codes. Avoid leaking connection details or diagnostics into the reason text.
- [ ] **Step 4: Run focused tests and frontend build.** Same test commands; `npm --prefix frontend run build`. Expected: pass.
- [ ] **Step 5: Commit only Task 3 files.** `git add` the listed files and `git commit -m "feat: explain execution waiting reasons"`.

### Task 4: Configure job controls in creation and management UI

**Files:**
- Create: `frontend/apps/tenant/src/features/jobs/JobExecutionControls.tsx`
- Modify: `frontend/apps/tenant/src/features/jobs/JobWizard.tsx`, `frontend/apps/tenant/src/features/jobs/ManagedJobDetail.tsx`, `frontend/apps/tenant/src/features/jobs/JobManagementPage.tsx`, `frontend/apps/tenant/src/App.tsx`
- Modify: `frontend/packages/ui/locales/en.json`, `frontend/packages/ui/locales/pt-BR.json`
- Modify: `docs/reference/execution-guarantees.md`
- Create test: `frontend/apps/tenant/src/features/jobs/JobExecutionControls.test.tsx`
- Modify test: `frontend/apps/tenant/src/features/jobs/JobWizard.test.tsx`

**Interfaces:**
- Consumes: `JobEditorDto` and `PUT /api/jobs/{id}/execution-controls` from Task 1.
- Produces: `JobExecutionControls` with `{jobId, limit, pauseOnNeedsReview, canManage, onSaved}` props; wizard controls default `{limit:1,pauseOnNeedsReview:false}` and manager detail controls read current values.

- [ ] **Step 1: Write failing UI tests.** Assert new wizard defaults to 1/off, existing detail renders stored limit/switch, Allow explains parallel capacity while Queue explains serial behavior, read-only users cannot mutate, zero capacity is clearly described as pausing starts, and a failed controls save leaves the new draft disabled with its choices intact for retry.
- [ ] **Step 2: Run focused tests.** `npm --prefix frontend exec -- vitest run apps/tenant/src/features/jobs/JobWizard.test.tsx apps/tenant/src/features/jobs/JobExecutionControls.test.tsx`. Expected: new assertions fail.
- [ ] **Step 3: Implement controls and explanatory copy.** Add the numeric field and switch beside Overlap in the wizard; label the switch “Wait for review before starting new runs” for Queue and “Skip new runs while review is pending” for Skip; omit it for Allow. Save the live controls after draft creation and before enabling. Use one atomic API mutation for both values. In job detail show both values, permit manager edits, and refresh after save; pass `canManage` from App. Keep the advanced policy JSON editor separate from live settings. Update English/Portuguese labels and execution guarantees.
- [ ] **Step 4: Run focused tests and frontend build.** Same test command; `npm --prefix frontend run build`. Expected: pass.
- [ ] **Step 5: Commit only Task 4 files.** `git add` the listed files and `git commit -m "feat: configure job capacity and review hold in UI"`.

## Final verification

- [ ] Apply migration 0005 to a disposable tenant database and compare its schema with `current-state.sql`; verify existing/new job defaults.
- [ ] Run `dotnet build WorkerDock.slnx -c Release` and `npm --prefix frontend run build`.
- [ ] Run the focused backend and frontend tests above when the required SQL test environment is available.
- [ ] Review the full diff against the spec, especially direct starts, retries, review resolution, concurrency races, accessibility, and English/Portuguese copy.
