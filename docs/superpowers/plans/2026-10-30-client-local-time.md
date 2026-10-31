# Client-Local Time Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show operational dates and times in the client machine's timezone throughout the tenant UI, without a display-timezone override.

**Architecture:** Resolve the browser's IANA timezone once per page load and pass it through existing timezone props. Centralize parsing and formatting of offset-bearing instants. Keep UTC API values and the schedule's configured zone authoritative for execution; convert only at the UI boundary and for calendar-day query ranges.

**Tech Stack:** React, TypeScript, MUI, `Intl`, Temporal polyfill, Vitest, Playwright, ASP.NET Core UTC APIs.

**Spec:** `docs/superpowers/specs/2026-10-30-client-local-time-design.md`

## Global Constraints

- Do not rewrite stored timestamps or alter scheduler evaluation, recurrence rules, selected offsets, or dynamic-value reference-date semantics.
- API instants remain UTC with an explicit `Z` or numeric offset. Date-only values and schedule-local wall times are distinct types and must not be parsed as UTC instants.
- Continue supporting English and Brazilian Portuguese formatting. Never apply a fixed `-03:00` offset; client zones may observe daylight saving time.
- The client timezone is detected from the browser. A machine timezone change during an open session takes effect after reload; no live OS timezone listener is required.
- Keep persisted tenant `displayTimeZone` and the settings request field for compatibility. The UI must not use either as a display source.

## Review Focus

- A stored `workerdock.timeZone=UTC` or session `timeZone=UTC` must not override `America/Sao_Paulo` in the browser (Task 1 test).
- An instant just before local midnight must show the previous calendar date, not its UTC date (Task 2 test).
- Null, malformed, and offset-less timestamp strings must never display `Invalid Date` or be silently treated as local instants (Task 1 test).
- A custom history day across a daylight-saving transition must use the correct 23- or 25-hour UTC range; rolling 24-hour filters stay rolling (Task 3 test).
- A saved schedule in another zone must retain that zone and selected offset; a repeated local hour must show distinct preview instants (Task 4 test).

---

### Task 1: Client zone source and settings

**Files:**
- Create: `frontend/apps/tenant/src/localTime.ts`
- Test: `frontend/apps/tenant/src/localTime.test.ts`
- Modify: `frontend/packages/ui/src/preferences.ts`, `frontend/packages/ui/src/index.ts`
- Modify: `frontend/apps/tenant/src/App.tsx`
- Modify: `frontend/apps/tenant/src/features/settings/PersonalSettings.tsx`, `frontend/apps/tenant/src/features/settings/TenantSettingsForm.tsx`
- Modify: `frontend/packages/ui/locales/en.json`, `frontend/packages/ui/locales/pt-BR.json`
- Test: `frontend/apps/tenant/src/auth/SessionProvider.test.tsx`, `frontend/apps/tenant/src/features/settings/SettingsSections.test.tsx`

**Interfaces:**
- Produces: `getClientTimeZone(): string`, using `Intl.DateTimeFormat().resolvedOptions().timeZone`; `formatInstant(value: string | Date | null | undefined, language: Language, timeZone: string, kind?: "dateTime" | "time"): string` in `localTime.ts`.
- `formatInstant` accepts `Date` objects and offset-bearing ISO strings only; missing, invalid, or offset-less strings return `"—"`. Use one locale-driven numeric date and clock format with seconds for `dateTime` and clock only for `time`.
- `getClientTimeZone` falls back to `UTC` only if the browser cannot supply a usable zone; Personal Settings displays the effective zone.
- `usePreferences()` produces language and translations only. `App` resolves one `clientTimeZone` per page load and supplies it to existing timezone props and later tasks, replacing every `preferences.timeZone` use.

- [ ] **Step 1: Write failing tests.** Assert `formatInstant("2026-10-30T03:15:00Z", "en", "America/Sao_Paulo")` contains `10/30/2026` and `12:15:00 AM`; assert `null`, `"bad"`, and `"2026-10-30T03:15:00"` return `"—"`. In the App test, seed `workerdock.timeZone=UTC` and a UTC session, mock the client zone as `America/Sao_Paulo`, then assert Settings has no editable timezone selector and shows `America/Sao_Paulo`. Assert General Settings has no display-timezone selector and its name-save request carries the unchanged `displayTimeZone` value.
- [ ] **Step 2: Run the focused tests.** `npm test -- --run apps/tenant/src/localTime.test.ts apps/tenant/src/auth/SessionProvider.test.tsx apps/tenant/src/features/settings/SettingsSections.test.tsx` from `frontend`; expect the new assertions to fail.
- [ ] **Step 3: Implement the interfaces and settings changes.** Remove `workerdock.timeZone` read/write and the preference setter; remove the unused `timeZones` export after its last display-setting consumer is gone. Keep the server session and settings contracts unchanged. Update English and Portuguese copy to say dates follow this device.
- [ ] **Step 4: Run the focused tests and tenant build.** Run the command from Step 2, then `npm run build --workspace @workerdock/tenant`; expect both to pass.
- [ ] **Step 5: Commit.** Stage only Task 1 files and commit `fix: derive display timezone from client`.

### Task 2: Operational timestamp displays

**Files:**
- Modify: `frontend/apps/tenant/src/features/monitoring/Dashboard.tsx`, `ExecutionHistoryPage.tsx`, `ExecutionDetails.tsx`, `JobsTable.tsx`, `WorkersPage.tsx`
- Modify: `frontend/apps/tenant/src/features/jobs/JobHistoryTable.tsx`, `ManagedJobDetail.tsx`, `JobManagementPage.tsx`
- Modify: `frontend/apps/tenant/src/App.tsx` to pass `clientTimeZone` into views that lack it
- Test: `frontend/apps/tenant/src/features/monitoring/Dashboard.test.tsx`, `ExecutionHistoryPage.test.tsx`, `ExecutionDetails.test.tsx`, `JobsTable.test.tsx`, `WorkersPage.test.tsx`
- Test: `frontend/apps/tenant/src/features/jobs/JobHistoryTable.test.tsx`, `ManagedJobDetail.test.tsx`
- Create: `frontend/e2e/client-local-time.spec.ts`

**Interfaces:**
- Consumes: Task 1 `formatInstant` and `clientTimeZone`.
- Produces: `timeZone: string` props for `ExecutionDetails`, `JobsTable`, `WorkersPage`, `ManagedJobDetail`, and the `JobManagementPage` route; no component reads session or local storage for display time.

- [ ] **Step 1: Write failing component and browser tests.** Cover dashboard created/upcoming timestamps, execution created/attempt/timeline timestamps, worker heartbeat, and managed job history. In Playwright, set browser `timezoneId: "America/Sao_Paulo"`, seed legacy `workerdock.timeZone=UTC`, return session `timeZone: "UTC"`, and assert dashboard and history render `2026-10-30T03:15:00Z` as `10/30/2026, 12:15:00 AM` and `2026-10-30T01:15:00Z` as `10/29/2026, 10:15:00 PM`.
- [ ] **Step 2: Run focused tests.** `npm test -- --run apps/tenant/src/features/monitoring/Dashboard.test.tsx apps/tenant/src/features/monitoring/ExecutionHistoryPage.test.tsx apps/tenant/src/features/monitoring/ExecutionDetails.test.tsx apps/tenant/src/features/monitoring/JobsTable.test.tsx apps/tenant/src/features/monitoring/WorkersPage.test.tsx apps/tenant/src/features/jobs/JobHistoryTable.test.tsx apps/tenant/src/features/jobs/ManagedJobDetail.test.tsx` from `frontend`; expect new assertions to fail.
- [ ] **Step 3: Replace scattered `toLocaleString`, `toLocaleTimeString`, and raw UTC display values with `formatInstant`.** Pass the client zone through the route; format refresh clocks with `kind: "time"`. Preserve durations as durations, IDs as IDs, and UTC values sent back to APIs. Do not change scheduler or backend code.
- [ ] **Step 4: Verify.** Run the focused tests, `npm run build --workspace @workerdock/tenant`, and `npx playwright test e2e/client-local-time.spec.ts --config playwright.config.ts` from `frontend`; expect correct local dates on both sides of midnight.
- [ ] **Step 5: Commit.** Stage only Task 2 files and commit `fix: render operational timestamps in client time`.

### Task 3: Audit, alert, and calendar-day consistency

**Files:**
- Modify: `frontend/apps/tenant/src/features/monitoring/AuditPage.tsx`
- Modify: `frontend/apps/tenant/src/features/alerts/IncidentHistoryTable.tsx`, `DeliveryHistoryTable.tsx`
- Test: `frontend/apps/tenant/src/features/monitoring/AuditPage.test.tsx`, `historyTime.test.ts`, `frontend/apps/tenant/src/features/alerts/IncidentHistoryTable.test.tsx`, `DeliveryHistoryTable.test.tsx`
- Modify: `frontend/e2e/client-local-time.spec.ts`

**Interfaces:**
- Consumes: Task 1 formatter and the client zone already passed from `App` to `toUtcRange` and `PeriodControl`.
- Produces: audit and alert timestamps using the same formatter, and a browser test proving that a chosen local calendar day produces the matching UTC API range.

- [ ] **Step 1: Write failing tests.** Cover incident opened/observed/resolved times, delivery next-attempt/completed times, audit occurrence, and null values. In the browser test, select custom day `2026-10-30` in `America/Sao_Paulo` and assert the API query contains `fromUtc=2026-10-30T03:00:00.000Z` and `toUtc=2026-10-31T03:00:00.000Z`. Extend the existing New York daylight-saving test with a one-day spring range (`2026-03-08T05:00:00Z` to `2026-03-09T04:00:00Z`) and a fall range (`2026-11-01T04:00:00Z` to `2026-11-02T05:00:00Z`); assert a 24-hour preset remains exactly 24 hours.
- [ ] **Step 2: Run focused tests.** `npm test -- --run apps/tenant/src/features/monitoring/AuditPage.test.tsx apps/tenant/src/features/monitoring/historyTime.test.ts apps/tenant/src/features/alerts/IncidentHistoryTable.test.tsx apps/tenant/src/features/alerts/DeliveryHistoryTable.test.tsx` from `frontend`; expect new display assertions to fail.
- [ ] **Step 3: Use `formatInstant` for all audit and alert dates and refresh clocks.** Keep `toUtcRange`'s existing Temporal conversion and rolling-window behavior; change it only if the new tests expose a defect.
- [ ] **Step 4: Verify.** Run the focused tests and `npx playwright test e2e/client-local-time.spec.ts --config playwright.config.ts` from `frontend`; expect local-day query bounds and displays to agree.
- [ ] **Step 5: Commit.** Stage only Task 3 files and commit `fix: align audit and alert dates with client zone`.

### Task 4: Schedule defaults and previews

**Files:**
- Modify: `frontend/apps/tenant/src/features/jobs/JobWizard.tsx`, `ManagedJobEditor.tsx`, `ManagedJobDetail.tsx`, `JobManagementPage.tsx`
- Modify: `frontend/apps/tenant/src/features/schedules/ScheduleEditor.tsx`, `SchedulePreview.tsx`
- Modify: `frontend/packages/ui/locales/en.json`, `frontend/packages/ui/locales/pt-BR.json`
- Test: `frontend/apps/tenant/src/features/jobs/JobWizard.test.tsx`, `ManagedJobEditor.test.tsx`, `ManagedJobDetail.test.tsx`; create `frontend/apps/tenant/src/features/schedules/SchedulePreview.test.tsx`
- Modify: `frontend/e2e/scheduling.spec.ts`

**Interfaces:**
- Consumes: Task 1 client zone and formatter. `App` passes `language: Language` through `JobManagementPage` to `JobWizard` and `ManagedJobDetail`, then to `ManagedJobEditor`. `ScheduleEditor` receives `clientTimeZone: string` and `language: Language`; `SchedulePreview` receives those plus `scheduleTimeZone: string` and preview items `{ utc, offset }`.
- Produces: newly added schedules default to the client zone; saved schedules retain `zoneId`, `localOnce`, and `selectedOffset`; previews show client time first and schedule-zone time with the returned offset when zones differ.

- [ ] **Step 1: Write failing tests.** Assert a new wizard schedule and a newly added schedule in `ManagedJobEditor` use `America/Sao_Paulo` when that is the client zone. Assert loading an existing `America/New_York` one-time schedule preserves its `01:30` wall time and `-05:00` selected offset. For previews at `2026-11-01T05:30:00Z` and `2026-11-01T06:30:00Z`, assert distinct client instants and both schedule-local `01:30` values with their `-04:00` and `-05:00` offsets. Keep the UTC instant visible as labelled technical detail.
- [ ] **Step 2: Run focused tests.** `npm test -- --run apps/tenant/src/features/jobs/JobWizard.test.tsx apps/tenant/src/features/jobs/ManagedJobEditor.test.tsx apps/tenant/src/features/jobs/ManagedJobDetail.test.tsx apps/tenant/src/features/schedules/SchedulePreview.test.tsx` from `frontend`; expect new assertions to fail.
- [ ] **Step 3: Implement schedule UI changes.** Pass the client zone into both schedule editors. Replace `ManagedJobEditor`'s `UTC` default for adding a schedule. Keep existing schedule input values unchanged. Label one-time wall time with the schedule zone, show client-local preview, and add a readable client/schedule-zone summary above the managed job's raw schedule input, labelled as technical wall time and zone data. For one-time summaries, derive the instant from `localOnce` plus `selectedOffset`; do not parse `localOnce` alone as a UTC instant.
- [ ] **Step 4: Verify.** Run the focused tests, `npm run build --workspace @workerdock/tenant`, `npx playwright test e2e/scheduling.spec.ts e2e/client-local-time.spec.ts --config playwright.config.ts`, then `npm test -- --run` from `frontend`; expect all to pass.
- [ ] **Step 5: Commit.** Stage only Task 4 files and commit `fix: default schedules and previews to client time`.

## Final check

- [ ] Run `rg -n "toLocaleString|toLocaleTimeString|createdAtUtc}|startedAtUtc}|item.utc|workerdock.timeZone" frontend/apps/tenant/src frontend/packages/ui/src` and inspect each remaining match: operational instants must use the shared formatter; UTC strings may remain only in labelled technical data or API payloads.
- [ ] Review the Task 1–4 changes against every goal and non-goal in the spec. Confirm all API requests still carry UTC instants and all persisted schedule zones and selected offsets remain unchanged.
