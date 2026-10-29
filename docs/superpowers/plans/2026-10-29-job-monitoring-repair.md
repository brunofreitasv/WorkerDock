# Job Monitoring Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make SQL-backed job monitoring load reliably and show older jobs that match the visible filters.

**Architecture:** Keep the read-only monitoring endpoint and query. Fix the SQL projection after reproducing the row conversion error, remove the hidden default job-age cutoff, and improve failure/retry presentation without altering audit date semantics.

**Tech Stack:** ASP.NET Core, C#, Microsoft.Data.SqlClient, SQL Server 2022+, xUnit, React, TypeScript, MUI, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-28-job-management-design.md`

## Global Constraints

- Keep Monitoring accessible under the existing Read policy; management links appear only to managers.
- Keep operational failed-24h counters and Audit's bounded date filters unchanged.
- Do not show raw SQL exceptions to browsers; log safe diagnostics with correlation information.
- Use existing MUI styling and English/pt-BR labels.
- SQL integration tests require a disposable SQL Server 2022+ connection in `WORKERDOCK_TEST_SQL`.

## Review Focus

- A scheduled job has `Paused=1`: monitoring returns a Boolean true and 200, not a row-conversion failure (Task 1).
- A manual job has no schedule: monitoring returns false for both schedule flags (Task 1).
- A job's current revision is older than seven days: the unfiltered jobs endpoint still returns it (Task 2).
- A malformed explicitly supplied date/filter/cursor: the endpoint rejects it with 400 rather than 503 or a broad query (Task 2).
- Refresh fails after a successful page: the UI keeps that page, marks it stale, and offers Retry (Task 3).

---

### Task 1: Reproduce and repair the SQL jobs projection

**Files:**
- Modify: `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`
- Create: `tests/WorkerDock.IntegrationTests/MonitoringJobsSqlTests.cs`

**Interfaces:**
- Consumes: `IMonitoringQueries.JobsAsync(MonitoringFilter, PageRequest, CancellationToken)` and `SqlDatabaseFixture`.
- Produces: unchanged `Page<JobSummaryDto>` with `HasSchedule` and `SchedulePaused` as real Boolean values.

- [ ] **Step 1: Write failing SQL integration tests** `JobsAsync_reads_scheduled_and_manual_rows` and `JobsAsync_reads_paused_schedule`. Seed one valid revision per job and a schedule for one; assert 200-equivalent successful query and exact Boolean flags.
- [ ] **Step 2: Run those tests against a disposable SQL instance.** Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~MonitoringJobsSqlTests`. Expected: a reproducible projection exception for a nonempty result; capture its type and offending column before changing code.
- [ ] **Step 3: Change the schedule flag expression** in `SqlMonitoringQueries.JobsAsync` so the value read by `GetBoolean(8)` is explicitly SQL `bit`; retain false when there is no live schedule. Verify selected column ordinal and types against the test.
- [ ] **Step 4: Run the focused tests.** Expected: both pass with SQL-backed rows.
- [ ] **Step 5: Commit** this isolated projection fix and its tests.

### Task 2: Remove the invisible age filter and preserve API validation

**Files:**
- Modify: `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`
- Modify: `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`
- Modify: `tests/WorkerDock.IntegrationTests/MonitoringJobsSqlTests.cs`
- Modify: `tests/WorkerDock.IntegrationTests/MonitoringAccessTests.cs`

**Interfaces:**
- Consumes: the same `JobsAsync` contract; `MonitoringFilter.FromUtc` and `ToUtc` remain for explicit date filters and Audit.
- Produces: `GET /api/monitoring/jobs?limit=50` with no implicit revision-age restriction; explicit date filters keep their current bounded validation. Add `bool ApplyDateFilter = true` to `MonitoringFilter`; the jobs endpoint sets it false only when both `fromUtc` and `toUtc` are absent. Audit keeps true.

- [ ] **Step 1: Write failing tests** `Jobs_endpoint_includes_revision_older_than_seven_days`, `Jobs_endpoint_rejects_invalid_explicit_date_range`, and `Jobs_endpoint_rejects_malformed_cursor`. Include a seeded job whose current revision is at least 91 days old.
- [ ] **Step 2: Run the tests.** Expected: the old-job assertion fails before the fix; invalid inputs remain safe.
- [ ] **Step 3: Make the jobs endpoint distinguish absent date parameters from explicit ones.** Set `ApplyDateFilter=false` only for jobs requests with neither date; condition the SQL revision-date predicate on it. Keep explicit range bounds and Audit defaults untouched. Log exceptions with the request correlation ID and return the existing safe 503 code.
- [ ] **Step 4: Run the focused SQL and endpoint tests.** Expected: older jobs visible, explicit filters valid, malformed inputs 400.
- [ ] **Step 5: Commit** the endpoint/query change and tests.

### Task 3: Distinguish monitoring errors from empty data

**Files:**
- Modify: `frontend/apps/tenant/src/features/monitoring/JobsTable.tsx`
- Create: `frontend/apps/tenant/src/features/monitoring/JobsTable.test.tsx`

**Interfaces:**
- Consumes: `useVisiblePolling` `{data, loading, error, stale, refresh}` and the existing monitoring response shape.
- Produces: a visible initial error/Retry state and stale prior data after refresh failure. The companion management plan adds links once its route exists.

- [ ] **Step 1: Write UI tests** for an initial 503, a failed refresh after populated data, and an actual empty successful page. Assert Retry invokes `refresh` and stale rows remain visible.
- [ ] **Step 2: Run the UI test.** Run: `npm.cmd --prefix frontend test -- --run apps/tenant/src/features/monitoring/JobsTable.test.tsx`. Expected: the new assertions fail.
- [ ] **Step 3: Implement the error/Retry presentation** in `JobsTable` without changing the existing read-only route.
- [ ] **Step 4: Run the UI test and frontend build.** Run: `npm.cmd --prefix frontend run build`. Expected: pass.
- [ ] **Step 5: Commit** the monitoring UI changes and tests.

## Final verification

- [ ] Run focused SQL tests, `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~Monitoring`, frontend tests, and `npm.cmd --prefix frontend run build` after all changes.
- [ ] Manually verify a nonempty SQL-backed `/api/monitoring/jobs?limit=50` response on the intended development tenant if available; do not claim the reported live tenant is fixed without that evidence.
