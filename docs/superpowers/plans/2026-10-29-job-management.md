# Job Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give managers a durable Jobs inventory where they can reopen, edit, enable, disable, archive, restore, and inspect job history.

**Architecture:** Add dedicated SQL-backed management reads and an atomic definition update. Extend the existing lifecycle for reversible archive and restore, then build inventory, detail, and editing views on the current React/MUI app. Keep monitoring reads separate; see the companion monitoring repair plan.

**Tech Stack:** ASP.NET Core, C#, Microsoft.Data.SqlClient, SQL Server 2022+, xUnit, React, TypeScript, MUI, Vitest, Playwright.

**Spec:** `docs/superpowers/specs/2026-10-28-job-management-design.md`

## Global Constraints

- New management reads and writes require the existing Manage policy, antiforgery on writes, actor audit, command receipts, tenant identity checks, and If-Match on edits/actions.
- No hard deletion, silent enabling, automatic template upgrade, or automatic run when restoring; preserve immutable revisions, execution snapshots, and existing retention policy.
- Reuse existing React/MUI styling, app hash navigation, English/pt-BR labels, schema-based template forms, timezone controls, and existing domain validation.
- Preserve complete saved values for dynamic bindings, profile/catalog, policy, missed-work tolerance/catch-up, pause state, and one-time offset/precision.
- SQL integration tests require a disposable SQL Server 2022+ connection in `WORKERDOCK_TEST_SQL`.

## Review Focus

- Job name has `%`, `_`, non-ASCII, or case differences: literal search and stable pagination still find each job exactly once (Task 1).
- Two managers edit the same job: the second write gets 412, retains the user's draft, and commits no partial schedule change (Tasks 4, 7).
- A template or profile is retired after a job is saved: the editor preserves saved values and never silently substitutes a new selection (Tasks 2, 7).
- An archived job has a legacy `Removed=1` schedule: restoration does not silently recreate it or run missed occurrences (Tasks 3, 7).
- A user without Manage permission follows a direct `#/jobs/<id>` link or calls a management API: the UI/API deny access without leaking configuration (Tasks 2, 6).

---

### Task 1: Dedicated management inventory query

**Files:**
- Create: `src/WorkerDock.Application/Jobs/IJobManagementQueries.cs`
- Create: `src/WorkerDock.Infrastructure/Jobs/SqlJobManagementQueries.cs`
- Create: `tests/WorkerDock.IntegrationTests/JobManagementQueryTests.cs`

**Interfaces:**
- Produces: `JobListFilter(string View, string? Search)`; `JobListItemDto(Guid JobId, string Name, string TemplateId, string TemplateVersion, bool Enabled, bool Archived, string? ScheduleKind, string? ScheduleZoneId, bool SchedulePaused, DateTimeOffset ConfigurationUpdatedUtc)`; `IJobManagementQueries.ListAsync(JobListFilter filter, PageRequest page, CancellationToken ct): ValueTask<Page<JobListItemDto>>`.
- Cursor contract: bounded opaque keyset token for name/JobId, tied to view and search; reject malformed or mismatched tokens with `ValidationFailed`.

- [ ] **Step 1: Write failing SQL tests** for current/enabled/disabled/archived filters, an old never-run draft, literal `%`/`_` search, non-ASCII names, stable adjacent pages, invalid/mismatched cursor, and no schedule.
- [ ] **Step 2: Run the focused tests.** Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~JobManagementQueryTests`. Expected: fail until the interface/query exists.
- [ ] **Step 3: Implement the list query** with parameterized search and name/JobId order, `limit+1`, no date cutoff or execution aggregation, tenant instance validation, and query bounds 1–100/100 chars.
- [ ] **Step 4: Run focused SQL tests.** Expected: pass.
- [ ] **Step 5: Commit** inventory query and tests.

### Task 2: Management list and coherent detail endpoints

**Files:**
- Modify: `src/WorkerDock.Application/Jobs/IJobManagementQueries.cs`
- Modify: `src/WorkerDock.Infrastructure/Jobs/SqlJobManagementQueries.cs`
- Create: `src/WorkerDock.Web/Endpoints/JobManagementEndpoints.cs`
- Modify: `src/WorkerDock.Web/Program.cs`
- Modify: `tests/WorkerDock.IntegrationTests/JobManagementQueryTests.cs`
- Create: `tests/WorkerDock.IntegrationTests/JobManagementAccessTests.cs`

**Interfaces:**
- Produces: `IJobManagementQueries.GetAsync(Guid jobId, CancellationToken ct): ValueTask<JobManagementDetailDto>`; `GET /api/jobs` uses Task 1 `ListAsync`; `GET /api/jobs/{id}` returns an ETag plus revision/configuration/policy/lifecycle/schedule/pause snapshot. Include a read-only previous schedule when an archived legacy tombstone exists.
- `JobManagementDetailDto` carries `JobEditorDto Editor`, `ScheduleEditorInput? Schedule`, `MissedWorkPolicy? MissedPolicy`, `bool SchedulePaused`, `ScheduleEditorInput? PreviousRemovedSchedule`.

- [ ] **Step 1: Write failing SQL and endpoint tests** for coherent detail/ETag, 404 unknown job, Manager access, Viewer denial, unavailable adapter 503, and archived legacy schedule snapshot.
- [ ] **Step 2: Run focused tests.** Expected: missing endpoint/query failures.
- [ ] **Step 3: Add the transactional read and endpoint** under Manage policy; ensure the four detail parts come from one consistent database snapshot and no configuration data enters list responses. Register a fail-closed `IJobManagementQueries` adapter for unsupported Memory mode.
- [ ] **Step 4: Run focused tests and `dotnet build WorkerDock.slnx`.** Expected: pass.
- [ ] **Step 5: Commit** read endpoints and tests.

### Task 3: Reversible archive and restore lifecycle

**Files:**
- Modify: `src/WorkerDock.Infrastructure/Jobs/SqlJobLifecycle.cs`
- Modify: `src/WorkerDock.Infrastructure/Jobs/SqlJobCommands.cs`
- Modify: `src/WorkerDock.Infrastructure/Jobs/InMemoryJobCommands.cs`
- Modify: `src/WorkerDock.Web/Endpoints/JobLifecycleEndpoints.cs`
- Modify: `tests/WorkerDock.IntegrationTests/JobLifecycleTests.cs`
- Modify: `tests/WorkerDock.IntegrationTests/JobCommandTests.cs`

**Interfaces:**
- Consumes: existing `IJobLifecycle.ApplyAsync(jobId, action, expectedVersion, actor, ct)` and existing action route.
- Produces: action `Restore` (Archived → Disabled only); Archive preserves a current schedule's definition/pause choice while blocking starts. Existing legacy `Removed=1` schedules remain removed after restore.

- [ ] **Step 1: Write failing SQL tests** for archive with a schedule, active execution, no post-archive materialization, restore disabled, no missed replay before explicit enable, legacy tombstone staying removed, audit/ETag changes, and invalid transition conflicts. Add a test that legacy configuration writes reject archived jobs.
- [ ] **Step 2: Run the focused tests.** Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~JobLifecycleTests`. Expected: new restore/preservation tests fail.
- [ ] **Step 3: Extend `ApplyAsync`** to accept Restore, preserve schedules on new archives while invalidating schedule revisions and moving recovery boundaries, and keep archive/restore state changes atomic. Reject SaveAsync updates on archived jobs in SQL and memory implementations. Verify scheduler, occurrence writer, queue, and retry gates already honor `Archived`/`Enabled` and adjust only if tests reveal a gap.
- [ ] **Step 4: Run lifecycle and command tests.** Expected: pass; archived schedule remains readable but cannot start.
- [ ] **Step 5: Commit** lifecycle changes and tests.

### Task 4: Atomic definition edit

**Files:**
- Create: `src/WorkerDock.Application/Jobs/IJobDefinitionCommands.cs`
- Create: `src/WorkerDock.Infrastructure/Jobs/SqlJobDefinitionCommands.cs`
- Create: `src/WorkerDock.Infrastructure/Scheduling/ScheduleInputConverter.cs`
- Modify: `src/WorkerDock.Web/Endpoints/ScheduleEndpoints.cs`
- Modify: `src/WorkerDock.Web/Endpoints/JobManagementEndpoints.cs`
- Modify: `src/WorkerDock.Web/Program.cs`
- Create: `tests/WorkerDock.IntegrationTests/JobDefinitionEditTests.cs`

**Interfaces:**
- Produces: `IJobDefinitionCommands.SaveDefinitionAsync(Guid jobId, JobRevision proposed, ScheduleEditorInput? schedule, MissedWorkPolicy? missedPolicy, byte[] expectedVersion, ActorContext actor, CancellationToken ct): ValueTask<CommandReceipt>`.
- `PUT /api/jobs/{id}/definition` body `{revision, schedule, missedPolicy}`; `schedule:null` means manual only, `missedPolicy:null` is required with it. Require If-Match and return the command receipt. `ScheduleInputConverter` centralizes the existing endpoint's timezone/one-time conversion without losing offset or unchanged precision.

- [ ] **Step 1: Write failing SQL tests** for enabled edit validation, unchanged enabled state, complete policy/schedule round-trip, save-without-schedule, unchanged schedule cursor/pause on name-only edit, partial failure rollback, stale ETag rollback, archived write 409, and old execution revision pinning.
- [ ] **Step 2: Run focused tests.** Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~JobDefinitionEditTests`. Expected: fail until the command exists.
- [ ] **Step 3: Implement one `BusinessTransaction`** that locks/verifies the job version, validates the proposed revision and schedule with existing authoring/profile rules, writes immutable revision and schedule changes together, retains state/pause/cursor when unchanged, bumps ETag, and records one receipt/audit outcome. Do not call separately committing legacy commands inside it. Extract shared schedule input conversion from `ScheduleEndpoints`.
- [ ] **Step 4: Run focused SQL tests, schedule tests, and `dotnet build WorkerDock.slnx`.** Expected: pass.
- [ ] **Step 5: Commit** atomic edit and tests.

### Task 5: Job-specific paginated execution history

**Files:**
- Modify: `src/WorkerDock.Application/Jobs/IJobManagementQueries.cs`
- Modify: `src/WorkerDock.Infrastructure/Jobs/SqlJobManagementQueries.cs`
- Modify: `src/WorkerDock.Web/Endpoints/JobManagementEndpoints.cs`
- Modify: `tests/WorkerDock.IntegrationTests/JobManagementQueryTests.cs`
- Modify: `tests/WorkerDock.IntegrationTests/JobManagementAccessTests.cs`

**Interfaces:**
- Produces: `IJobManagementQueries.ExecutionsAsync(Guid jobId, PageRequest page, CancellationToken ct): ValueTask<Page<JobExecutionItemDto>>`; `GET /api/jobs/{id}/executions?limit=50&cursor=...` under Manage. DTO fields: execution ID, job revision, trigger kind, state, created/start time, nullable duration. Descending created time/ID cursor; existing `/api/monitoring/executions/{id}` remains the detail target.

- [ ] **Step 1: Write failing SQL tests** for archived-job history, no executions, unknown job 404, identical timestamps across pages, and viewer denial. Assert execution attempts/audit history remain after archive.
- [ ] **Step 2: Run the focused tests.** Expected: missing method/route failures.
- [ ] **Step 3: Implement the bounded history query and route** with no implicit age cutoff or configuration leakage.
- [ ] **Step 4: Run query and access tests.** Expected: pass.
- [ ] **Step 5: Commit** history query and tests.

### Task 6: Durable inventory and navigation

**Files:**
- Create: `frontend/apps/tenant/src/features/jobs/JobManagementPage.tsx`
- Create: `frontend/apps/tenant/src/features/jobs/JobInventory.tsx`
- Create: `frontend/apps/tenant/src/features/jobs/jobManagementTypes.ts`
- Modify: `frontend/apps/tenant/src/App.tsx`
- Modify: `frontend/packages/ui/locales/en.json`
- Modify: `frontend/packages/ui/locales/pt-BR.json`
- Create: `frontend/apps/tenant/src/features/jobs/JobInventory.test.tsx`

**Interfaces:**
- Consumes: `GET /api/jobs` page from Tasks 1–2 and app `navigate(route)`.
- Produces: `#/jobs` list, `#/jobs/<id>` detail, `#/jobs/new` create. Preserve current/Enabled/Disabled/Archived filter, search, and page in URL or route state that survives reload and browser Back/Forward. Show no job configurations in browser storage.

- [ ] **Step 1: Write failing UI tests** for saved draft after reload, status views, literal/non-ASCII search, pagination state, old job visibility, empty versus failed load, and direct route access denial.
- [ ] **Step 2: Run focused tests.** Run: `npm.cmd --prefix frontend test -- --run apps/tenant/src/features/jobs/JobInventory.test.tsx`. Expected: fail before components exist.
- [ ] **Step 3: Implement list/navigation** with debounced search, stable real links, explicit loading/error/Retry/stale states, accessible row controls, responsive layout, and translated labels. Change hash navigation to add history entries for job routes. Keep the existing wizard behind Create job; the `#/jobs/<id>` route may show a loading placeholder until Task 7 adds the detail view.
- [ ] **Step 4: Run focused tests and frontend build.** Expected: pass.
- [ ] **Step 5: Commit** inventory/nav/UI translations and tests.

### Task 7: Detail and existing-job editor

**Files:**
- Create: `frontend/apps/tenant/src/features/jobs/ManagedJobDetail.tsx`
- Create: `frontend/apps/tenant/src/features/jobs/ManagedJobEditor.tsx`
- Create: `frontend/apps/tenant/src/features/jobs/JobDefinitionDraft.ts`
- Modify: `frontend/apps/tenant/src/features/jobs/JobWizard.tsx`
- Modify: `frontend/apps/tenant/src/features/jobs/JobManagementPage.tsx`
- Modify: `frontend/packages/ui/locales/en.json`
- Modify: `frontend/packages/ui/locales/pt-BR.json`
- Create: `frontend/apps/tenant/src/features/jobs/ManagedJobEditor.test.tsx`

**Interfaces:**
- Consumes: `GET /api/jobs/{id}` and `PUT /api/jobs/{id}/definition` from Tasks 2 and 4, existing `TemplateForm`, `ScheduleEditor`, `DynamicValuePreview`, and ETag client.
- Produces: read-only Overview/Configuration/Schedule/Execution policy/History sections and an editable Configuration/Schedule/Execution policy form. `JobDefinitionDraft` represents complete saved values and dirty sections without dropping untouched fields.

- [ ] **Step 1: Write failing UI tests** for loading a persisted ID after refresh, retained dynamic/profile/catalog/policy/missed-work/one-time values, unavailable descriptor display, one atomic save, unchanged enabled state, unsaved navigation guard, validation summary, stale ETag draft preservation, and disabled state after restore.
- [ ] **Step 2: Run the focused tests.** Run: `npm.cmd --prefix frontend test -- --run apps/tenant/src/features/jobs/ManagedJobEditor.test.tsx`. Expected: fail before the editor exists.
- [ ] **Step 3: Implement detail/editor** with direct edit sections and a review step, reusable schema controls, exact unchanged-value round-tripping, persistent Save/Cancel bar, browser/in-app unsaved guards, explicit stale-state reload/reapply, and no implicit status mutation. Refactor only wizard helpers needed for shared value preservation; keep existing creation tests passing. Show legacy removed schedule as previous settings requiring deliberate reapplication.
- [ ] **Step 4: Run editor and existing wizard tests plus frontend build.** Expected: pass.
- [ ] **Step 5: Commit** detail/editor and tests.

### Task 8: Actions, history UI, docs, and end-to-end flow

**Files:**
- Modify: `frontend/apps/tenant/src/features/jobs/ManagedJobDetail.tsx`
- Modify: `frontend/apps/tenant/src/features/jobs/JobInventory.tsx`
- Modify: `frontend/apps/tenant/src/features/jobs/JobActions.tsx`
- Modify: `frontend/apps/tenant/src/features/monitoring/JobsTable.tsx`
- Modify: `frontend/packages/ui/locales/en.json`
- Modify: `frontend/packages/ui/locales/pt-BR.json`
- Modify: `docs/how-to/manage-jobs.md`
- Create: `frontend/apps/tenant/src/features/jobs/ManagedJobActions.test.tsx`
- Modify: `frontend/e2e/job-management.spec.ts`

**Interfaces:**
- Consumes: list/detail/history/edit endpoints and `POST /api/jobs/{id}/actions` including Restore; existing Run now and execution details routes.
- Produces: row/detail Enable, Disable, Delete/Archive, Restore, Duplicate, Pause/Resume schedule, Run now, and bounded History with links to execution details.

- [ ] **Step 1: Write failing UI/browser tests** for create → reopen → edit → disable → enable → archive → find in Archived → restore → edit, plus archive confirmation effects, stale action ETag, history after archive, Retry, English/pt-BR labels, keyboard activation, and narrow viewport actions.
- [ ] **Step 2: Run focused UI tests and Playwright test.** Expected: new behavior fails before implementation.
- [ ] **Step 3: Wire actions and history** with server-confirmed refreshes, per-row busy states, translated safe errors, and appropriate confirmation copy; update user documentation. Monitoring Manage links from the companion plan should resolve to detail, while viewers see no Manage control.
- [ ] **Step 4: Run frontend tests, build, browser test, focused SQL tests, and `dotnet build WorkerDock.slnx`.** Expected: pass; note any browser/SQL environment prerequisite separately.
- [ ] **Step 5: Commit** the integrated experience and documentation.

## Final verification

- [ ] Run all focused tests and required builds once after the final change; use `superpowers:verification-before-completion` before a success claim.
- [ ] Review both implementation plans against the approved spec, check the diff for unintended files, and obtain the selected review gate before considering the work complete.
