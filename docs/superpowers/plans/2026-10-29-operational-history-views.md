# Operational History Views Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make execution, alert, and audit histories scannable, filterable, and paginated while preserving their access rules.

**Architecture:** Extend each existing read API with SQL-side filters and opaque keyset pages. Reuse date-range and page controls in the tenant React app. Keep the Dashboard as a ten-row preview linked to a new Read-access cross-job execution history.

**Tech Stack:** .NET/ASP.NET Core, SQL Server, React 19, TypeScript, MUI, Vitest, xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-10-29-operational-history-design.md`

## Global Constraints

- Job-specific History stays Manage-only; Dashboard, full execution history, execution details, Alerts, and Audit are Read-access.
- Keep the Dashboard at ten recent rows; list pages contain 50 rows by default and at most 100.
- Defaults: execution and job history last 7 days; deliveries last 30 days; audit last 7 days; incidents show all Open/Unknown regardless of age.
- Custom date ranges use the selected user timezone, with inclusive start and exclusive end in UTC. Audit supports a span of up to 365 days without changing its 365-day retention default.
- Incident period uses latest activity, with Open before Unknown before Resolved and then newest activity first.
- List responses contain only safe fields/codes; no secrets, exception bodies, or private payloads.
- Preserve unrelated uncommitted work in the designated worktree. Stage only files owned by each task.

## Review Focus

- A custom date spanning a daylight-saving transition includes both local calendar days exactly; Task 2 tests this.
- A cursor from another filter or tenant fails validation instead of returning confusing rows; Task 1 tests this.
- An Open incident older than 30 days remains on the initial Alerts view; Task 6 tests this.
- An audit event six months old is found by a matching custom range; Task 7 tests this.
- A Read user can browse full execution history but cannot fetch manager-only Job History or mutate a job; Task 3 tests this.

## File Map

- `src/WorkerDock.Infrastructure/Persistence/HistoryCursorCodec.cs`: common tenant/filter-bound cursor encoding.
- `frontend/apps/tenant/src/features/monitoring/historyTime.ts`, `PeriodControl.tsx`, `CursorPager.tsx`: shared date conversion and list controls.
- Monitoring DTO/query/endpoint files: Dashboard preview, full execution history, audit query and event-type options.
- Job management DTO/query/endpoint files: filtered job-specific history.
- Alert DTO/query/endpoint files: independently filtered incident and delivery pages.
- `frontend/apps/tenant/src/features/monitoring/ExecutionHistoryPage.tsx`, `Dashboard.tsx`, `AuditPage.tsx`: execution and audit screens.
- `frontend/apps/tenant/src/features/jobs/JobHistoryTable.tsx`, `ManagedJobDetail.tsx`: manager-only job history.
- `frontend/apps/tenant/src/features/alerts/IncidentHistoryTable.tsx`, `DeliveryHistoryTable.tsx`, `AlertRulesPage.tsx`: alert histories separate from editors.
- `frontend/apps/tenant/src/App.tsx`, monitoring types, both locale JSON files, and relevant help articles: routes, types, copy, and documentation.

---

### Task 1: Filter-bound history cursors

**Files:** Create `src/WorkerDock.Infrastructure/Persistence/HistoryCursorCodec.cs`; test `tests/WorkerDock.UnitTests/HistoryCursorCodecTests.cs`.

**Interfaces:** Produce `HistoryCursorCodec.Encode(Guid instanceId, string scope, string filterKey, DateTimeOffset time, Guid id, int rank = 0): string` and `HistoryCursorCodec.Decode(string? cursor, Guid instanceId, string scope, string filterKey): HistoryPosition?`, where `HistoryPosition` has `Time`, `Id`, and `Rank`. Hash the canonical filter key in the cursor; reject invalid, oversized, cross-tenant, cross-scope, and cross-filter cursors with `WorkerDockException("ValidationFailed", "cursor")`.

- [ ] **Step 1: Write failing tests** `RoundTrips_sort_position` with `Assert.Equal(new HistoryPosition(time, id, 2), decoded)`; `Rejects_changed_filter_tenant_scope_and_malformed_cursor` with `Assert.Equal("ValidationFailed", error.Code)` for each mismatch and a 2049-character cursor.
- [ ] **Step 2: Run** `dotnet test tests/WorkerDock.UnitTests/WorkerDock.UnitTests.csproj --filter FullyQualifiedName~HistoryCursorCodecTests`; expect the tests to fail before implementation.
- [ ] **Step 3: Implement** the two signatures and `HistoryPosition` in the new file; encode version, instance, scope, SHA-256 filter fingerprint, sort position, and validate on decode.
- [ ] **Step 4: Run** the same test command; expect pass.
- [ ] **Step 5: Commit** only the codec and its test: `feat: bind history cursors to filters`.

### Task 2: Reusable period and pagination controls

**Files:** Create `frontend/apps/tenant/src/features/monitoring/historyTime.ts`, `historyTime.test.ts`, `PeriodControl.tsx`, `PeriodControl.test.tsx`, `CursorPager.tsx`; modify `frontend/apps/tenant/package.json`, `frontend/package-lock.json` to add `@js-temporal/polyfill` for consistent browser support.

**Interfaces:** Produce `HistoryPeriod = { kind: "24h" | "7d" | "30d" } | { kind: "custom"; startDate: string; endDate: string }`, `toUtcRange(period: HistoryPeriod, timeZone: string, now: Date): { fromUtc: string; toUtc: string }`, `PeriodControl({ value, timeZone, maxDays, onChange })`, and `CursorPager({ previousCount, nextCursor, onPrevious, onNext })`. End date is inclusive in the calendar and becomes exclusive next local midnight in the API.

- [ ] **Step 1: Write failing tests** for presets and invalid/reversed or over-limit custom dates; assert `expect(toUtcRange(custom("2026-03-08", "2026-03-09"), "America/New_York", now)).toEqual({ fromUtc: "2026-03-08T05:00:00.000Z", toUtc: "2026-03-10T04:00:00.000Z" })`. Control test asserts `expect(onChange).toHaveBeenCalled()` and boundary pager buttons are disabled.
- [ ] **Step 2: Run** `npm test -- --run apps/tenant/src/features/monitoring/historyTime.test.ts apps/tenant/src/features/monitoring/PeriodControl.test.tsx` from `frontend`; expect fail.
- [ ] **Step 3: Implement** the interfaces using Temporal timezone conversion and MUI controls; reject invalid custom dates before requests.
- [ ] **Step 4: Run** the same Vitest command and `npm run build --workspace @workerdock/tenant`; expect pass.
- [ ] **Step 5: Commit** only shared controls, tests, and dependency files: `feat: add history period and page controls`.

### Task 3: Full execution query and richer Dashboard DTO

**Files:** Modify `src/WorkerDock.Application/Monitoring/MonitoringDtos.cs`, `IMonitoringQueries.cs`, `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`, `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`, `src/WorkerDock.Web/Program.cs` unavailable adapter; test `tests/WorkerDock.IntegrationTests/ExecutionHistorySqlTests.cs`, `MonitoringAccessTests.cs`. Add a SQL migration/index only if the query plan shows it is needed.

**Interfaces:** Produce `ExecutionHistoryFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Search, string? State, string? TemplateId)`, `ExecutionHistoryItemDto(ExecutionId, JobId, JobName, TemplateId, State, TriggerKind, CreatedAtUtc, StartedAtUtc, Duration)`, and `IMonitoringQueries.ExecutionsAsync(ExecutionHistoryFilter filter, PageRequest page, CancellationToken ct): ValueTask<Page<ExecutionHistoryItemDto>>`. Add `TemplateId` and nullable `Duration` to `RecentExecutionDto`. Map `GET /api/monitoring/executions?fromUtc=&toUtc=&search=&state=&templateId=&limit=&cursor=` with Read access; the existing `/{executionId}` route remains unchanged. Defaults: 7 days/all states, max span 90 days. Search matches job name or exact execution ID. Filter cursor by all normalized inputs.

- [ ] **Step 1: Write failing SQL tests** that seed two job revisions and three states; assert `Assert.Equal(oldTemplateId, row.TemplateId)`, `Assert.Equal(expectedDuration, row.Duration)`, `Assert.DoesNotContain(outsideBoundaryId, page.Items.Select(x => x.ExecutionId))`, `Assert.Equal(ids.Count, ids.Distinct().Count())`, and `Assert.Equal("ValidationFailed", changedFilterError.Code)`. Add access assertions `Assert.Equal(ServiceUnavailable, readHistory.StatusCode)` without SQL and `Assert.Equal(Forbidden, jobHistory.StatusCode)` plus forbidden job mutation for a Read user.
- [ ] **Step 2: Run** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter "FullyQualifiedName~ExecutionHistorySqlTests|FullyQualifiedName~MonitoringAccessTests"`; expect fail before implementation in a SQL-enabled environment.
- [ ] **Step 3: Implement** DTOs, SQL query, endpoint validation, and Dashboard projection; reuse Task 1 cursor codec and a single aggregate attempt query per page for duration.
- [ ] **Step 4: Run** the same integration test command; expect pass with the configured SQL test store.
- [ ] **Step 5: Commit** only execution API, DTO, and test files: `feat: query filtered execution history`.

### Task 4: Dashboard preview and full execution history screen

**Files:** Create `frontend/apps/tenant/src/features/monitoring/ExecutionHistoryPage.tsx`, `ExecutionHistoryPage.test.tsx`; modify `Dashboard.tsx`, `Dashboard.test.tsx`, `monitoringTypes.ts`, `frontend/apps/tenant/src/App.tsx`, `frontend/packages/ui/locales/en.json`, `pt-BR.json`, `frontend/apps/tenant/src/help/content/en/monitoring.md`, `pt-BR/monitoring.md`; update `frontend/e2e/monitoring.spec.ts`.

**Interfaces:** `ExecutionHistoryPage({ language, timeZone, onExecution }: { language: string; timeZone: string; onExecution(id: string): void })`. Add `timeZone: string` and `onHistory(): void` to Dashboard props. Route `#/executions` from Dashboard only; Read-access route and no job modification actions. Use Task 2 controls and Task 3 API.

- [ ] **Step 1: Write failing UI tests** asserting `expect(screen.getByRole("table", { name: "Recent executions" })).toBeVisible()`, ten rows max, template/terminal duration, and View all navigation. History test asserts requested period/status/template/search, `expect(onExecution).toHaveBeenCalledWith(id)`, Previous/Next page changes, cursor reset on filter change, empty/error/stale messages, and no manager actions for Read users.
- [ ] **Step 2: Run** `npm test -- --run apps/tenant/src/features/monitoring/Dashboard.test.tsx apps/tenant/src/features/monitoring/ExecutionHistoryPage.test.tsx` from `frontend`; expect fail.
- [ ] **Step 3: Implement** the two views, route, translations, responsive table overflow, and help copy; format dates in `timeZone` and preserve Dashboard polling.
- [ ] **Step 4: Run** the same Vitest command, `npm run build --workspace @workerdock/tenant`, and `npm run e2e -- --grep "execution history"` from `frontend`; expect pass.
- [ ] **Step 5: Commit** only execution UI, copy, and test files: `feat: browse execution history from dashboard`.

### Task 5: Filtered manager-only job history

**Files:** Modify `src/WorkerDock.Application/Jobs/IJobManagementQueries.cs`, `src/WorkerDock.Infrastructure/Jobs/SqlJobManagementQueries.cs`, `src/WorkerDock.Web/Endpoints/JobManagementEndpoints.cs`; create `frontend/apps/tenant/src/features/jobs/JobHistoryTable.tsx`, `JobHistoryTable.test.tsx`; modify `ManagedJobDetail.tsx`, `JobManagementPage.tsx`, `tests/WorkerDock.IntegrationTests/JobManagementQueryTests.cs`, `JobManagementAccessTests.cs`, and relevant EN/pt-BR job help copy.

**Interfaces:** Produce `JobExecutionFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? State)` and change `ExecutionsAsync(Guid jobId, JobExecutionFilter filter, PageRequest page, CancellationToken ct)`. Existing route accepts `fromUtc`, `toUtc`, and `state`, defaults 7 days/all states, max span 90 days; remains Manage-only. `JobHistoryTable({ jobId, language, timeZone }: { jobId: string; language: string; timeZone: string })` uses Task 2 controls and links to `#/execution:{id}`.

- [ ] **Step 1: Write failing tests** with `Assert.All(page.Items, x => Assert.Equal("Failed", x.State))`, `Assert.Equal("ValidationFailed", changedFilterError.Code)`, and `Assert.Contains(archivedExecutionId, page.Items.Select(x => x.ExecutionId))`; UI asserts only one page's rows remain after Next. Use fixed 2020 dates with an explicit filter in the existing fixture test.
- [ ] **Step 2: Run** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~JobManagementQueryTests` and `npm test -- --run apps/tenant/src/features/jobs/JobHistoryTable.test.tsx` from `frontend` for the second command; expect fail.
- [ ] **Step 3: Implement** the query/endpoint and extracted table; preserve the current Jobs permissions and all other job-detail tabs.
- [ ] **Step 4: Run** both targeted commands and tenant build; expect pass.
- [ ] **Step 5: Commit** only job-history files and tests: `feat: filter and page job history`.

### Task 6: Independent alert history pages

**Files:** Modify `src/WorkerDock.Application/Alerts/IAlertQueries.cs`, `src/WorkerDock.Infrastructure/Alerts/SqlAlertQueries.cs`, `src/WorkerDock.Web/Endpoints/AlertEndpoints.cs`; create `frontend/apps/tenant/src/features/alerts/IncidentHistoryTable.tsx`, `DeliveryHistoryTable.tsx`, their tests, and `tests/WorkerDock.IntegrationTests/AlertHistorySqlTests.cs`; modify `AlertRulesPage.tsx`, `AlertRulesPage.test.tsx`, `frontend/apps/tenant/src/App.tsx`, `src/WorkerDock.Web/Program.cs` unavailable adapter, and EN/pt-BR alert help copy.

**Interfaces:** `IncidentFilter(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string State, Guid? RuleId)` with `State="Active"` as default and null bounds; `DeliveryFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Status, string? Kind)` default 30 days. Replace alert list methods with `IncidentsAsync(IncidentFilter, PageRequest, CancellationToken): ValueTask<Page<AlertIncidentDto>>` and `DeliveriesAsync(DeliveryFilter, PageRequest, CancellationToken): ValueTask<Page<AlertDeliveryDto>>`. Add safe `RuleName` and `SubjectKey` to delivery rows. `IncidentHistoryTable({ language, timeZone })` and `DeliveryHistoryTable({ language, timeZone })` receive `{ language: string; timeZone: string }`; AlertRulesPage passes these props. Both Read routes return page shape and validate supported states/kinds/statuses; each uses Task 1 cursor and Task 2 UI controls.

- [ ] **Step 1: Write failing tests** with `Assert.Contains(oldOpenId, defaultPage.Items.Select(x => x.IncidentId))`, `Assert.Equal(new[] { "Open", "Unknown", "Resolved" }, allPage.Items.Select(x => x.State))`, `Assert.Equal(expectedRule, delivery.RuleName)`, and `Assert.Equal("ValidationFailed", changedFilterError.Code)`; also assert latest-activity period behavior, independent delivery pages, and Read UI without editors.
- [ ] **Step 2: Run** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AlertHistorySqlTests` and `npm test -- --run apps/tenant/src/features/alerts/IncidentHistoryTable.test.tsx apps/tenant/src/features/alerts/DeliveryHistoryTable.test.tsx apps/tenant/src/features/alerts/AlertRulesPage.test.tsx`; expect fail.
- [ ] **Step 3: Implement** the alert contracts, SQL queries, routes, and extracted tables; update existing alert tests/mocks from arrays to page responses. Keep rule/sink editing behavior intact.
- [ ] **Step 4: Run** targeted tests, tenant build, and `npm run e2e -- e2e/alerts.spec.ts`; expect pass.
- [ ] **Step 5: Commit** only alert-history files, copy, and tests: `feat: filter and page alert histories`.

### Task 7: Audit event type and year-scale period search

**Files:** Modify `src/WorkerDock.Application/Monitoring/MonitoringDtos.cs`, `IMonitoringQueries.cs`, `src/WorkerDock.Infrastructure/Monitoring/SqlMonitoringQueries.cs`, `src/WorkerDock.Web/Endpoints/MonitoringEndpoints.cs`, `src/WorkerDock.Web/Program.cs` unavailable adapter, `frontend/apps/tenant/src/features/monitoring/AuditPage.tsx`, `frontend/apps/tenant/src/App.tsx`; create `AuditPage.test.tsx` and `tests/WorkerDock.IntegrationTests/AuditHistorySqlTests.cs`; update EN/pt-BR audit help copy and `frontend/e2e/monitoring.spec.ts`.

**Interfaces:** Produce `AuditHistoryFilter(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? Search, string? EventType)` and `AuditEventTypesAsync(CancellationToken): ValueTask<IReadOnlyList<string>>`. Change `AuditAsync` to take `AuditHistoryFilter`; map `GET /api/audit/event-types` (Read) and exact `eventType` on `/api/audit`. `AuditPage({ language, timeZone }: { language: string; timeZone: string })` uses Task 2 controls. Default seven days, custom span up to 365 days, half-open dates, Task 1 cursor binding, and event types from retained tenant rows.

- [ ] **Step 1: Write failing tests** that seed an event six months old; assert `Assert.Contains(oldEventId, page.Items.Select(x => x.EventId))`, `Assert.All(typePage.Items, x => Assert.Equal("ManualRunAccepted", x.EventType))`, `Assert.Contains("JobRevisionSaved", eventTypes)`, and `Assert.Equal("ValidationFailed", tooWideOrChangedCursorError.Code)`; UI asserts filter change resets pagination while retaining actor/correlation search.
- [ ] **Step 2: Run** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AuditHistorySqlTests` and `npm test -- --run apps/tenant/src/features/monitoring/AuditPage.test.tsx`; expect fail.
- [ ] **Step 3: Implement** the audit contract, query, endpoint, and page with Task 2 controls, selected timezone display, and EN/pt-BR text; preserve safe audit projection and Read policy.
- [ ] **Step 4: Run** targeted tests, tenant build, and `npm run e2e -- --grep "audit"`; expect pass.
- [ ] **Step 5: Commit** only audit files, copy, and tests: `feat: filter audit history by type and period`.

## Final Verification

- [ ] Run `dotnet test tests/WorkerDock.UnitTests/WorkerDock.UnitTests.csproj` and the SQL integration tests above with the configured SQL store; resolve any failures introduced by signature changes.
- [ ] Run `npm test -- --run`, `npm run build`, and relevant Playwright suites from `frontend`.
- [ ] Review the Dashboard, full history, Job History, Alerts, and Audit at desktop and narrow widths in both languages; confirm table links, empty/error states, and permissions.
- [ ] Review the diff and commits against the approved spec; preserve unrelated worktree changes.
