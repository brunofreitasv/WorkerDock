# WorkerDock Web UX Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Give every WorkerDock web screen a coherent, calm operations interface with clearly separated filters, actions, and data.

**Architecture:** Move the visual theme and generic page primitives into @workerdock/ui, then apply them to the tenant shell, histories, operational pages, administration pages, and Control Center. Preserve existing routes, APIs, business logic, and permission checks. Use focused component tests and visual inspection to protect behavior.

**Tech Stack:** React 19, TypeScript, MUI, Vite, Vitest, Testing Library, Playwright.

**Spec:** docs/superpowers/specs/2026-10-29-web-ux-refresh-design.md

## Global Constraints

- Use the user-designated linked worktree at C:/Users/bruno/.codex/worktrees/template-max-properties/WorkerDock 2.
- Use Sol for implementation and review agents.
- Preserve existing uncommitted work, especially job-management files; stage only task-owned changes.
- Keep English and pt-BR tenant copy and Read/Manage access intact.
- Keep existing API contracts, retention, active-incident semantics, and dashboard-to-history flow.
- Use existing MUI; add no visual dependency.
- Run frontend tests serially because the full parallel suite has known timeouts.

## Review Focus

- Narrow viewport with a long tenant name: shell menu and page content remain reachable (Task 1 test).
- Custom period with missing, inverted, or over-limit dates: active query stays valid and inline guidance appears (Task 2 test).
- Read-only user on jobs and alerts: no new edit action appears through layout changes (Task 6 test).
- Active incident older than the preset period: default view still includes it (Task 4 test).
- Empty and stale result sets: panels keep headings, recovery action, and pagination state (Task 3 test).

---

## File map

- frontend/packages/ui/src/theme.ts: shared MUI theme.
- frontend/packages/ui/src/PageLayout.tsx: page header, section surface, filter panel, results panel, status badge, empty state.
- frontend/packages/ui/src/AppShell.tsx and index.ts: mobile drawer, desktop sidebar, exports.
- frontend/apps/tenant/src/App.tsx: consume theme and use cohesive page header/settings tabs.
- frontend/apps/center/src/main.tsx: consume shared theme.
- frontend/apps/tenant/src/features/monitoring/PeriodControl.tsx and CursorPager.tsx: selected period, custom range, page footer.
- frontend/apps/tenant/src/features/monitoring/HistoryLayout.tsx: tenant-specific history filter/result composition and copy.
- Individual feature pages listed below: apply primitives without changing data contracts.

### Task 1: Shared theme, primitives, and shell

**Files:** Create frontend/packages/ui/src/theme.ts, PageLayout.tsx, PageLayout.test.tsx; modify frontend/packages/ui/src/index.ts, AppShell.tsx, AppShell.test.tsx; modify frontend/apps/tenant/src/App.tsx and frontend/apps/center/src/main.tsx.

**Interfaces:** Export workerdockTheme, PageHeader({title, description?, actions?}), SectionSurface({title?, description?, actions?, children}), FilterPanel({title, children, actions?}), ResultsPanel({title, meta?, actions?, children, footer?}), StatusBadge({label, tone}), EmptyState({title, description?}). Keep existing AppShell props and route behavior.

- [ ] Step 1: Add failing tests for shell mobile menu, long display name, selected route, keyboard skip link, and primitives' headings/actions.
- [ ] Step 2: Run npm test -- --run packages/ui/src/AppShell.test.tsx packages/ui/src/PageLayout.test.tsx --maxWorkers=1 from frontend; verify failures target absent new behavior.
- [ ] Step 3: Implement shared theme with primary #0B6C61, background #F5F8F7, primary text #182D39, 12px surface radius, system font stack, visible focus; implement primitives and responsive shell drawer; wire both apps to theme.
- [ ] Step 4: Run the focused tests and npm run build --workspace @workerdock/tenant plus npm run build --workspace @workerdock/center, then commit only Task 1 files.

### Task 2: History controls and reusable composition

**Files:** Modify frontend/apps/tenant/src/features/monitoring/PeriodControl.tsx, PeriodControl.test.tsx, CursorPager.tsx, CursorPager.test.tsx; create HistoryLayout.tsx and HistoryLayout.test.tsx.

**Interfaces:** Keep PeriodControl props and HistoryPeriod type. Export HistoryFilters({title, children, onClear?, clearLabel?}) and HistoryResults({title, updatedAt?, refreshLabel, onRefresh, footer?, children}). Screen-specific filters remain owned by each page.

- [ ] Step 1: Change tests to require visibly selected presets, dates hidden until Custom, inline invalid/incomplete/over-limit feedback, no onChange before a valid custom range, and a labeled pager footer; test HistoryFilters and HistoryResults separation.
- [ ] Step 2: Run focused Vitest and confirm new assertions fail.
- [ ] Step 3: Implement responsive period controls and HistoryLayout using the shared primitives. Keep instant valid range application and cursor reset through each page's existing onChange; do not alter toUtcRange rules.
- [ ] Step 4: Run focused tests and tenant TypeScript build; commit Task 2 files.

### Task 3: Execution, job, and audit histories

**Files:** Modify frontend/apps/tenant/src/features/monitoring/ExecutionHistoryPage.tsx, ExecutionHistoryPage.test.tsx, AuditPage.tsx, AuditPage.test.tsx; modify frontend/apps/tenant/src/features/jobs/JobHistoryTable.tsx, JobHistoryTable.test.tsx.

**Interfaces:** Consume Task 2 HistoryFilters and HistoryResults. Keep component props and existing API query parameters. Reset cursor on changed filters and Clear.

- [ ] Step 1: Add tests for grouped filters, reset to default period/status/search, Refresh in results toolbar, selected period, empty and stale states, and unchanged pagination/query parameters.
- [ ] Step 2: Run these three focused test files and confirm new assertions fail.
- [ ] Step 3: Apply separate filter and results surfaces, clear actions, status badges, table scroll cues, and consistent row spacing. Retain all displayed data and permissions.
- [ ] Step 4: Run focused tests and tenant build; commit Task 3 files.

### Task 4: Alerts activity and configuration

**Files:** Modify frontend/apps/tenant/src/features/alerts/IncidentHistoryTable.tsx, DeliveryHistoryTable.tsx, AlertRulesPage.tsx and their existing test files.

**Interfaces:** Consume Task 2 history layout. Keep alert request URLs and existing Read/Manage checks. Default Active incident view remains unbounded by period until the operator enables period filtering.

- [ ] Step 1: Add tests for incident and delivery filter/result separation, older active incidents under default query, visible refresh/empty states, and Manager configuration in its own section.
- [ ] Step 2: Run focused alert test files and confirm the new assertions fail.
- [ ] Step 3: Apply shared panels, surface headings, readable status, separated rule/destination forms, and responsive form rows; keep incident activity ahead of deliveries and configuration.
- [ ] Step 4: Run focused tests and tenant build; commit Task 4 files.

### Task 5: Operational overview and monitoring

**Files:** Modify frontend/apps/tenant/src/features/monitoring/Dashboard.tsx, JobsTable.tsx, WorkersPage.tsx, ExecutionDetails.tsx and corresponding tests.

**Interfaces:** Consume shared page/section/status primitives. Keep existing props, data calls, current health and open items first, full-history link, execution detail links, and Manager-only controls.

- [ ] Step 1: Add tests for dashboard hierarchy, separate recent/upcoming sections, visible full-history action, and read-only detail/monitoring actions.
- [ ] Step 2: Run focused tests and confirm new assertions fail.
- [ ] Step 3: Refactor layout into consistent overview cards and sections; group execution metadata and controls; improve table/form spacing without losing fields.
- [ ] Step 4: Run focused tests and tenant build; commit only Task 5 changes, preserving unrelated edits in modified files.

### Task 6: Jobs, settings, templates, and help

**Files:** Modify frontend/apps/tenant/src/App.tsx, features/settings/PersonalSettings.tsx, TenantSettingsForm.tsx, VaultSettingsPage.tsx, OutboundPolicyPage.tsx, RetentionPage.tsx, features/profiles/ProfileEditor.tsx, help/HelpPage.tsx and relevant tests. Apply job-area layout from tracked parent screens and shared theme; do not edit pre-existing untracked job-management files. Touch additional form files only where layout requires it.

**Interfaces:** Keep existing routes, editor props, dirty-state guard, and permission checks. Settings navigation uses MUI Tabs/Tab with accessible tab semantics and horizontal scroll at narrow widths.

- [ ] Step 1: Add tests for settings tab keyboard/selected state, viewer's lack of job/alert modification controls, and sectioned job detail/editor forms.
- [ ] Step 2: Run focused tests and confirm new assertions fail.
- [ ] Step 3: Apply page sections, readable form groups, consistent action placement, tabs, and empty/error states. Preserve all user-edited lines, untracked job-management files, and JobWizard behavior.
- [ ] Step 4: Run focused tests and tenant build; commit only Task 6 changes.

### Task 7: Control Center and final visual verification

**Files:** Modify frontend/apps/center/src/App.tsx, TenantDirectory.tsx, RegistrationEditor.tsx, ControlCenter.test.tsx; adjust preceding task files only for visual defects found.

**Interfaces:** Consume shared theme and page primitives. Keep tenant search, pagination, health, registration, and permissions unchanged.

- [ ] Step 1: Add a test for grouped directory search/actions, readable health status, and viewer/manager action separation.
- [ ] Step 2: Run ControlCenter.test.tsx and confirm new assertions fail.
- [ ] Step 3: Align header, directory, actions, and registration form with the shared system.
- [ ] Step 4: Run center tests and both app builds.
- [ ] Step 5: Inspect desktop and narrow screenshots for Dashboard, execution history, alerts, job history, audit, settings, and Control Center; fix clipping, spacing, contrast, and keyboard defects found.
- [ ] Step 6: Run npm test -- --run --maxWorkers=1 and npm run build from frontend; commit Task 7 and verified visual fixes.
