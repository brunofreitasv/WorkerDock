# Monitoring Load Reduction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce monitoring HTTP requests and share dashboard/worker SQL reads across clients of one tenant web process.

**Architecture:** Give each visible monitoring view a specified refresh interval. A singleton web-host cache holds only dashboard and worker snapshots for five seconds and shares concurrent misses; route authorization and existing DTOs remain at the HTTP boundary.

**Tech Stack:** React 19, TypeScript, Vitest, ASP.NET Core/.NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-31-monitoring-load-design.md`

## Global Constraints

- Keep existing tenant web/runtime/SQL deployment, API route paths and DTOs, read authorization, and `no-store` responses.
- Keep visibility pause/resume, immediate first load, manual refresh, no overlapping client requests for an unchanged view key, stale-data warning, and failure backoff capped at 60 seconds.
- Cache only `GET /api/dashboard` and `GET /api/monitoring/workers`; no filtered/paged cache, broker, SignalR, or cross-process coordination.
- Cache only successful responses, for five seconds from query completion; a disconnected waiter cannot cancel other waiters' shared query.
- Preserve unrelated local changes, including `docs/tutorials/run-with-sql-server.md`.

## File Map

- `frontend/apps/tenant/src/features/monitoring/useVisiblePolling.ts`: interval-aware shared polling behavior.
- `frontend/apps/tenant/src/features/monitoring/useVisiblePolling.test.tsx`: timing, visibility, cancellation, and backoff behavior.
- Eight tenant view components listed in Task 1: choose their polling intervals without changing their API requests.
- `src/WorkerDock.Web/MonitoringSnapshotCache.cs`: two bounded process-wide snapshots and coalescing logic.
- `src/WorkerDock.Web/Program.cs` and `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`: register and use the cache after authorization.
- `tests/WorkerDock.IntegrationTests/MonitoringSnapshotCacheTests.cs`: cache concurrency and expiry behavior with stub queries and a controllable clock.
- `tests/WorkerDock.IntegrationTests/MonitoringAccessTests.cs`: retain route authorization and `no-store` behavior.

## Review Focus

- Fifty simultaneous requests after cache expiry should trigger one query, not fifty (Task 2 test).
- A disconnected caller should stop waiting without canceling the shared query (Task 2 test).
- A failed replacement should return an error, never serve an expired snapshot, and allow the next call to retry (Task 2 test).
- A hidden tab should make no requests and should reload immediately when visible (Task 1 test).
- A filter change while the prior request is pending should fetch the new key and ignore the old response (Task 1 test).

---

### Task 1: Tune tenant polling

**Files:** Modify `frontend/apps/tenant/src/features/monitoring/useVisiblePolling.ts`, `useVisiblePolling.test.tsx`, `Dashboard.tsx`, `WorkersPage.tsx`, `JobsTable.tsx`, `ExecutionDetails.tsx`, `ExecutionHistoryPage.tsx`, `AuditPage.tsx`, `frontend/apps/tenant/src/features/alerts/IncidentHistoryTable.tsx`, and `DeliveryHistoryTable.tsx`.

**Interfaces:** `useVisiblePolling<T>(load: () => Promise<T>, reloadKey?: string, intervalMs?: number): VisiblePollingState<T>`; default interval remains 5,000 ms. The third argument is a view's successful-refresh delay and initial failure-backoff base.

- [ ] **Step 1: Write failing behavioral tests.** In `useVisiblePolling.test.tsx`, assert a 15,000 ms interval makes only one request at 5,000 ms and a second at 15,000 ms; a failed 15,000 ms request retries at 30,000 ms and caps at 60,000 ms; hidden/resumed tabs and manual refresh behave as before. Assert a new `reloadKey` starts its own request while an older key is pending and ignores the older response. With fake timers, count 50 visible hooks over one minute: 600 recurring requests at the old five-second default versus 200 at 15 seconds, excluding initial loads.
- [ ] **Step 2: Confirm failure.** Run `npm.cmd --prefix frontend run test -- --run apps/tenant/src/features/monitoring/useVisiblePolling.test.tsx`; expect the new interval tests to fail against the two-argument hook.
- [ ] **Step 3: Implement the interval.** Use `intervalMs` for the reset delay and backoff base; keep at most one scheduled timer and one in-flight request per `reloadKey`. Use a request generation to ignore a superseded key's completion and timer. Pass `15000` in Dashboard, WorkersPage, JobsTable, ExecutionDetails, and IncidentHistoryTable; `30000` in DeliveryHistoryTable; `60000` in ExecutionHistoryPage and AuditPage. Filter, cursor, and manual refresh still fetch immediately.
- [ ] **Step 4: Verify.** Run the targeted test command above, then `npm.cmd --prefix frontend run test -- --run` and `npm.cmd --prefix frontend run build`; expect passing tests and TypeScript build. Record the new theoretical visible request rates: 4/minute per 15-second view, 2/minute per 30-second view, 1/minute per 60-second view, excluding first load.
- [ ] **Step 5: Commit.** Stage only Task 1 files and commit `perf: reduce tenant monitoring polling frequency`.

### Task 2: Coalesce and cache dashboard/worker reads

**Files:** Create `src/WorkerDock.Web/MonitoringSnapshotCache.cs` and `tests/WorkerDock.IntegrationTests/MonitoringSnapshotCacheTests.cs`.

**Interfaces:** Public singleton `MonitoringSnapshotCache(IMonitoringQueries queries, TimeProvider clock, IHostApplicationLifetime lifetime)` exposes `Task<DashboardDto> DashboardAsync(CancellationToken requestCancellation)` and `Task<RuntimeStatusDto> WorkersAsync(CancellationToken requestCancellation)`. It owns independent five-second snapshots, using the host stopping token for the shared SQL query and the request token only for that caller's wait. Use `TimeProvider` elapsed time for expiry.

- [ ] **Step 1: Write failing cache tests.** A stub `IMonitoringQueries` counts DashboardAsync/WorkersAsync calls; a controllable `TimeProvider` advances time. Assert cache hit before five seconds, a new query at five seconds, independent keys, fifty concurrent callers sharing one in-flight query, waiter cancellation isolation, failure retry, and no stale value after expiry plus failure.
- [ ] **Step 2: Confirm failure.** Run `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~MonitoringSnapshotCacheTests`; expect missing `MonitoringSnapshotCache`.
- [ ] **Step 3: Implement `MonitoringSnapshotCache`.** Hold one completed value and one in-flight task per snapshot under a small lock. Start a shared query with `lifetime.ApplicationStopping`, cache only success at completion, clear failed in-flight tasks, and use `WaitAsync(requestCancellation)` for each HTTP caller. Do not return expired values.
- [ ] **Step 4: Verify.** Rerun the targeted `dotnet test` command; expect all cache tests to pass.
- [ ] **Step 5: Commit.** Stage only Task 2 files and commit `perf: coalesce monitoring snapshot queries`.

### Task 3: Wire the cache at the authorized routes

**Files:** Modify `src/WorkerDock.Web/Program.cs`, `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`, and `tests/WorkerDock.IntegrationTests/MonitoringAccessTests.cs`.

**Interfaces:** Register `MonitoringSnapshotCache` as a singleton. Only `/api/dashboard` and `/api/monitoring/workers` call its corresponding methods; other `IMonitoringQueries` routes retain their direct behavior and authorization policies.

- [ ] **Step 1: Write failing endpoint tests.** In `MonitoringAccessTests`, use the existing HTTPS test host and a counting stub query service to assert two authorized dashboard requests and two worker requests each cause one query, each response retains `Cache-Control: no-store`, and an unauthorized caller cannot receive a cached result. Assert a failed snapshot still returns the existing 503 code.
- [ ] **Step 2: Confirm failure.** Run `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~MonitoringAccessTests`; expect the new query-count assertion to fail.
- [ ] **Step 3: Register and use the cache.** Replace only the two endpoint query calls; retain `RequireAuthorization(AccessPolicies.Read)` and the existing 503 envelope. Do not add browser caching headers.
- [ ] **Step 4: Verify.** Rerun the targeted command, then `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store!=Sql'`, `dotnet build src/WorkerDock.Web -c Release`, `npm.cmd --prefix frontend run test -- --run`, `npm.cmd --prefix frontend run build`, and `git diff --check`; expect success. Report the measured 50-hook request count and 50-caller cache query count against the uncached baseline. State that live SQL throughput remains unmeasured until deployment testing and that each web process has its own cache.
- [ ] **Step 5: Commit.** Stage only Task 3 files and commit `perf: share monitoring snapshots across requests`.
