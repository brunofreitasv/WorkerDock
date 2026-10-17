# WorkerDock Scheduling and Operator UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add durable schedules and the complete operator workflow on top of proven manual execution.

**Architecture:** Quartz schedule triggers invoke an idempotent occurrence dispatcher; application cursors apply missed-work and lifecycle policies. React uses the same authoritative schedule preview and read/manage APIs as external clients.

**Tech Stack:** Existing .NET/Quartz/SQL runtime, React/MUI/TanStack Query, xUnit and Playwright.

**Spec:** [Approved design](../specs/2026-10-17-workerdock-design.md), sections 4–7 and 10. Depends on [Stage 03](2026-10-17-workerdock-03-templates.md).

## Global Constraints

- All [index](2026-10-17-workerdock-implementation.md) constraints apply.
- "For recurring schedules, nonexistent local occurrences are skipped, and repeated local occurrences run once at the earlier UTC instant. This applies to interval expressions too."
- "Queued executions already accepted remain governed by the table above." Apply spec section 6.2 controls without treating misfire policy as execution retention.
- "Read-only users receive no management controls and cannot bypass the same policy through APIs."

## Review Focus

Project focus 4 (locale and ambiguous times) is pinned in Task 15 using Task 04's evaluator. Tasks 14/16 also exercise stale projection and stale monitoring so pending work is not misrepresented as applied/healthy.

## File structure

Task 14 adds the schedule/application cursor and lifecycle commands, leaving Quartz evaluation in Task 04. Task 15 extends existing manual forms into the wizard and job management. Task 16 adds separate safe read projections for the dashboard, details and audit, with no entity/configuration leakage.

### Task 14: Durable schedules, bounded missed work and lifecycle commands

**Files:** Create `src/WorkerDock.Domain/Scheduling/MissedWorkPolicy.cs`, `src/WorkerDock.Application/Scheduling/IScheduleCommands.cs`, `src/WorkerDock.Infrastructure/Scheduling/SqlScheduleCommands.cs`, `OccurrenceWriter.cs`, `src/WorkerDock.Quartz/Scheduling/OccurrenceDispatchJob.cs`, `ScheduleProjection.cs`, `src/WorkerDock.Application/Jobs/IJobLifecycle.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobLifecycle.cs`, `src/WorkerDock.Web/Endpoints/ScheduleEndpoints.cs`, `JobLifecycleEndpoints.cs`; `database/tenant/0005_scheduling.up.sql`, `0005_scheduling.down.sql`, manifest; `tests/WorkerDock.IntegrationTests/OccurrencePolicyTests.cs`, `JobLifecycleTests.cs`; `docs/scheduling.md`.

**Interfaces:**
- Consumes Task 04 `ScheduleDefinition`/`IScheduleEvaluator`, Tasks 05–06 revision/intent and Task 09 admission.
- `MissedWorkPolicy(string Mode, TimeSpan LatenessTolerance, int CatchUpCount, TimeSpan CatchUpAge)`; Mode Skip/Coalesce/CatchUp, default tolerance 60s, positive bounded count/age mandatory for CatchUp.
- `IScheduleCommands.SaveAsync(Guid jobId, ScheduleDefinition definition, MissedWorkPolicy policy, byte[]? expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; `PreviewAsync(ScheduleDefinition definition, DateTimeOffset afterUtc, int count, CancellationToken ct) -> ValueTask<IReadOnlyList<DateTimeOffset>>`.
- `OccurrenceWriter.MaterializeAsync(Guid scheduleId, long scheduleRevision, DateTimeOffset throughUtc, CancellationToken ct) -> ValueTask<int>` atomically advances cursor/inserts execution/events/intents.
- `ScheduleProjection.ApplyAsync(Guid scheduleId, long revision, CancellationToken ct) -> ValueTask` consumes schedule-kind outbox envelopes through the common bounded projection job; execution-kind payloads remain Task 06 DispatchIntent. Add schedule-kind decoding beside, not inside, the execution publisher; stale revisions recheck business state before `IScheduler` calls.
- `IJobLifecycle.ApplyAsync(Guid jobId, string action, byte[] expectedVersion, ActorContext actor, CancellationToken ct) -> ValueTask<CommandReceipt>`; action PauseSchedule/ResumeSchedule/Disable/Enable/Archive/Duplicate. Duplicate target ID identifies a new disabled job.

- [ ] **Step 1 — Write SQL/Quartz tests.** `OccurrencePolicyTests.Recovery_wakeup_applies_business_policy_once` runs two dispatchers after downtime with Skip/Coalesce/CatchUp, 60s tolerance and a test count bound of 3. `JobLifecycleTests.Disable_wins_over_stale_Quartz_trigger` holds projection, disables and attempts dispatch. Cover pause/resume no catch-up, manual during pause, held retry release on enable, archive with running attempt and no further retries, edit revision races, profile pinning and delayed initial projection.

```csharp
Assert.Equal(3, boundedCatchUp.CreatedExecutions);
Assert.Equal(1, coalesced.CreatedExecutions);
Assert.Equal(0, duplicateDispatcher.AdditionalExecutions);
Assert.Equal(0, startsAfterDisableCommit);
Assert.Equal(0, pausedIntervalReplay);
Assert.False(duplicateJob.Enabled);
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter "FullyQualifiedName~OccurrencePolicyTests|FullyQualifiedName~JobLifecycleTests"`; expect cursor/control failures.
- [ ] **Step 3 — Implement.** Project native cron/simple triggers through the outbox; recurring misfire uses FireAndProceed, one-time uses FireNow, actual occurrence identity comes from the filtered evaluator/cursor. Bound catch-up enumeration and row production, record skipped/coalesced ranges, preserve scheduled UTC and immutable job/profile revisions. Store activation/resume boundaries transactionally; guard stale trigger revision at materialization and child launch. Lifecycle controls update business state first and project Quartz effects afterward. Retrying accepted work never re-enters schedule-missed policy. Document both delay metrics and exact pause/disable/delete semantics.
- [ ] **Step 4 — Verify green.** Repeat tests plus calendar persistence and publication recovery suites. Expected PASS through restart and misfire thresholds, one logical occurrence under competing dispatchers, no silent lost accepted work.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add durable schedules and job lifecycle policies"`.

### Task 15: Guided creation, schedule editing and management actions

**Files:** Create `frontend/apps/tenant/src/features/jobs/JobWizard.tsx`, `JobDetails.tsx`, `JobActions.tsx`, `features/schedules/ScheduleEditor.tsx`, `SchedulePreview.tsx`, `features/executions/StopDialog.tsx`; modify ManualJobEditor/TemplateForm/api client/routes/locales; create `frontend/e2e/job-management.spec.ts`, `scheduling.spec.ts`; update `docs/manual-jobs.md`, `docs/scheduling.md`.

**Interfaces:**
- Consumes Tasks 13–14 forms/template metadata and schedule/lifecycle endpoints; Task 08 stop/resolve; Task 09 capacity commands.
- `ScheduleEditor(props: { value: ScheduleDefinitionDto; onChange(value: ScheduleDefinitionDto): void; preview(value: ScheduleDefinitionDto): Promise<string[]> }): ReactElement`; UTC ISO strings are authoritative and selected zone is explicit.
- `POST /api/schedules/preview` requires Manage, returns authoritative UTC instants plus display offset, and does not mutate scheduling. `GET /api/time-zones` returns validated zone IDs for Read users. `POST /api/jobs/{id}/actions` calls lifecycle; run/stop/force-stop/reconcile use existing explicit endpoints.

- [ ] **Step 1 — Write browser scenarios.** `scheduling.spec.ts` switches en→pt-BR while editing a repeated-hour time, preserves selected instant and shows next-run offsets. `job-management.spec.ts` creates template→config→schedule→policy→review, duplicates disabled, edits with stale ETag, runs now twice with one key, pauses/resumes, disables/enables, confirms delete and force stop. Viewers see history without configuration/actions. Infinite timeout is an explicit selectable option, not a magic zero.

```typescript
expect(savedUtcAfterLocaleSwitch).toBe(savedUtcBeforeLocaleSwitch);
await expect(page.getByText('No timeout', { exact: true })).toBeVisible();
await expect(page.getByRole('dialog')).toContainText('remote outcome');
expect(duplicate.enabled).toBe(false);
expect(staleEditResponse.status()).toBe(412);
expect(runIds[0]).toBe(runIds[1]);
```

- [ ] **Step 2 — Verify red.** `npm.cmd --prefix frontend run e2e -- scheduling.spec.ts job-management.spec.ts`; expect wizard/schedule/control failures.
- [ ] **Step 3 — Implement.** Six wizard stages with examples, defaults, safe server validation and policy descriptions. Show five-field grammar, AND day semantics, selected zone/offset, authoritative preview and paused/pending-projection states. Preserve draft after conflict, require review before duplicate activation, and distinguish graceful stop/force/local termination/remote outcome. Shared controls use capability flags but server remains authoritative. All copy/help/validation uses en/pt-BR resources and accessible focus/error summaries.
- [ ] **Step 4 — Verify green.** Repeat browser tests and frontend build; keyboard-only create/edit/run/stop flows pass at desktop and narrow viewport, no locale conversion changes UTC values.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add guided scheduling and job management workflows"`.

### Task 16: Safe operator dashboard, history and health views

**Files:** Create `src/WorkerDock.Application/Monitoring/IMonitoringQueries.cs`, `MonitoringDtos.cs`, `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`, `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`, `HealthEndpoints.cs`; `frontend/apps/tenant/src/features/monitoring/Dashboard.tsx`, `JobsTable.tsx`, `WorkersPage.tsx`, `ExecutionDetails.tsx`, `AuditPage.tsx`, `useVisiblePolling.ts`; `tests/WorkerDock.IntegrationTests/MonitoringAccessTests.cs`, `frontend/e2e/monitoring.spec.ts`; `docs/monitoring.md`.

**Interfaces:**
- Consumes Tasks 05/08 execution state/events, Task 09 capacity, Task 14 schedules and Task 01 audit.
- `PageRequest(string? Cursor, int Limit)` limits 1–100; `Page<T>(IReadOnlyList<T> Items, string? NextCursor)`; `MonitoringFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Search, string? State, string? TemplateId)` caps range to configured retention/search limits.
- `IMonitoringQueries.DashboardAsync(CancellationToken ct) -> ValueTask<DashboardDto>`, `JobsAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct) -> ValueTask<Page<JobSummaryDto>>`, `ExecutionAsync(Guid executionId, CancellationToken ct) -> ValueTask<ExecutionDetailsDto>`, `AuditAsync(MonitoringFilter filter, PageRequest page, CancellationToken ct) -> ValueTask<Page<AuditSummaryDto>>`.
- DTOs explicitly include safe names/IDs/state/reason/timing/counts/correlation, attempts/stop/uncertainty timeline and desired/effective capacity; exclude SQL/body/config/profile/vault values. Define DTO fields in `MonitoringDtos.cs` beside these queries, deriving only allowlisted projections.
- `TenantHealthDto(Guid InstanceId, string Overall, IReadOnlyDictionary<string,string> Components, DateTimeOffset ObservedUtc)` is the minimal health contract consumed by Task 20. Health-only credentials authenticate only its endpoint, even when panel access is anonymous.

- [ ] **Step 1 — Write access/query/UI tests.** Viewer reads every monitoring route but cannot obtain protected configuration or mutate by crafted request. Inspect safe DTOs with sentinel SQL/body/profile data. Test cursor ties, invalid/range-excess input, long-running infinite attempt, unavailable scheduler/store, backlog and pending policy. Browser fake time verifies 5s visible refresh, hidden pause, failure backoff and stale timestamp.

```csharp
Assert.DoesNotContain(secretSentinel, allMonitoringJson);
Assert.DoesNotContain(protectedSqlText, allMonitoringJson);
Assert.Equal("Degraded", healthWithoutCompatibleCapacity.Overall);
Assert.Equal(401, healthCredentialWithoutUserSession.StatusCode);
Assert.Equal(expectedDistinctIds, pagedIds.Distinct().Count());
```

- [ ] **Step 2 — Verify red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~MonitoringAccessTests` and `npm.cmd --prefix frontend run e2e -- monitoring.spec.ts`; expect safe-projection/polling failures.
- [ ] **Step 3 — Implement.** Paginated queries, parameterized filters, current health/upcoming/recent/success/failure/long-running dashboards, execution timeline/log paging, worker/capability and audit views. Cache with TanStack Query and refresh only visible screens every 5s initially. Add safe failure actions only for managers, honest empty/stale states and keyboard/non-color indicators. Readiness includes schema/stores/catalog/heartbeats/projection lag. Anonymous remains openly read/manage under its policy; health credential confers no extra rights. Task 17 adds retained aggregate/log plumbing without changing safe query contracts.
- [ ] **Step 4 — Verify green.** Repeat server/browser checks plus full access-policy matrix. Expected PASS, bounded query shapes and no secret/config leakage in network responses or DOM.
- [ ] **Step 5 — Commit.** Stage task files; `git commit -m "feat: add safe operational dashboards and execution history"`.

## Stage exit

- [ ] Demonstrate complete scheduled SQL/HTTP workflow, status/retry/stop history, viewer audit access and manager-only controls in both languages.
- [ ] Run calendar/cursor/control/auth regressions and browser/build checks. Remaining limitations: advanced alerting, archiving and central directory arrive in Stage 05.
