# Tenant Navigation, Settings, and Display Name Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Group tenant navigation, place access mode and personal preferences only in Settings, and show the configured tenant display name in the header and browser tab.

**Architecture:** Keep existing hash routes and permissions. App owns one read-accessible tenant-settings snapshot and passes its display name to AppShell; the settings form updates that snapshot only after a successful save. AppShell renders labelled navigation groups, while a Personal Settings view owns the access and preference controls.

**Tech Stack:** React 19, TypeScript, MUI, Vitest/Testing Library, Playwright, ASP.NET Core, xUnit.

**Spec:** [Approved design](../specs/2026-10-20-template-removal-tenant-navigation-design.md), commit fbdbcfd.

## Global Constraints

- Work in C:\Users\bruno\.codex\worktrees\template-max-properties\WorkerDock 2 on branch codex/workerdock-final.
- Keep route IDs and direct links unchanged, including help:<slug> and execution:<id>.
- Monitoring: Dashboard, Job monitoring, Workers, Alerts.
- Maintenance: Jobs, Templates.
- Administration: Documentation, Audit, Settings.
- Read users can open Personal Settings; only Manage users can open tenant configuration, vault, outbound, SQL profiles, and retention.
- Access mode, role status, anonymous warning, language, and personal time zone appear only in Settings.
- Header text beside the logo and document.title equal the saved display name; use WorkerDock while loading or after read failure.
- Keep English and Brazilian Portuguese copy in sync.
- Do not alter Memory-mode 503 responses or polling.
- Tasks are sequential. Commit each reviewed, passing task; stage only its files.

## Review Focus

- A Read-only user must see Personal Settings but no management control or forbidden API request. Test in Task 4.
- A 412 or failed save must leave the header and browser title on the last persisted name. Test in Task 4.
- A failed GET settings must leave WorkerDock visible and must not prevent ordinary navigation. Test in Task 2.
- A help or execution deep link must preserve its hash and mark the correct navigation item active. Test in Task 3.
- A long display name at a narrow viewport must remain readable without overlapping sign-in/out controls. Test in Task 5.

---

## Execution and file map

Run each named test red before implementation and green afterward. Frontend commands run from repository root through `npm.cmd --prefix frontend`. If Playwright browsers or a live backend are unavailable, record that gate as blocked and keep its test in the plan. Use `git -c safe.directory='C:/Users/bruno/.codex/worktrees/template-max-properties/WorkerDock 2'` if Git reports dubious ownership.

| Unit | Files and responsibility |
| --- | --- |
| Settings API | src/WorkerDock.Web/Program.cs changes GET /api/settings authorization only; AccessPolicyTests.cs verifies Read/Manage boundaries. |
| Tenant state and branding | New frontend/apps/tenant/src/features/settings/useTenantSettings.ts owns one settings snapshot, ETag, reload, and successful-save update; App.tsx owns document title; AppShell.tsx renders the name. |
| Navigation | App.tsx builds groups from session permissions; AppShell.tsx renders group headings and stable links. Locales supply group names. |
| Personal Settings | New PersonalSettings.tsx contains access information and local display preferences; new TenantSettingsForm.tsx holds the existing tenant form. App.tsx composes the tabs. |
| Tests and docs | SessionProvider.test.tsx, an AppShell test, responsive e2e coverage, bilingual settings help, and relevant content assertions. |

### Task 1: Let readers load tenant display information

**Files:** Modify src/WorkerDock.Web/Program.cs and tests/WorkerDock.IntegrationTests/AccessPolicyTests.cs.

**Interfaces:** GET /api/settings uses AccessPolicies.Read and returns the existing displayName, displayTimeZone, Version fields and ETag. PUT /api/settings stays AccessPolicies.Manage and keeps If-Match/antiforgery behavior.

- [ ] **Step 1: Write failing access test.** Name it `Reader_can_get_display_settings_but_cannot_update`. Sign in a Viewer; assert GET is 200 with displayName and no secret field, and PUT is 403. Assert an unauthenticated protected request does not receive settings.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~Reader_can_get_display_settings_but_cannot_update`; expect GET to fail authorization.
- [ ] **Step 3: Change only GET authorization.** Replace RequireAuthorization(AccessPolicies.Manage) on GET /api/settings with Read; leave PUT unchanged.
- [ ] **Step 4: Run green.** Run Task 1 command; expect pass.
- [ ] **Step 5: Commit.** Stage Task 1 files, run `git diff --cached --check`, commit `feat: let readers load tenant display settings`.

### Task 2: Share tenant settings and show the name globally

**Files:** Create frontend/apps/tenant/src/features/settings/useTenantSettings.ts and its test. Modify frontend/apps/tenant/src/App.tsx, frontend/packages/ui/src/AppShell.tsx, and frontend/apps/tenant/src/auth/SessionProvider.test.tsx.

**Interfaces:** Export `TenantSettings = { displayName: string; displayTimeZone: string; version: string }` and `useTenantSettings(enabled: boolean)` returning `{ current?: { data: TenantSettings; etag: string | null }; loading: boolean; error: boolean; reload(): void; acceptSave(data: TenantSettings): void }`. Fetch once only after session.canRead is true; acceptSave replaces the snapshot and derives the next quoted ETag from version. AppShell adds `displayName: string`.

- [ ] **Step 1: Write failing tests.** Name tests `Tenant_name_loads_on_any_reader_page`, `Failed_settings_load_uses_WorkerDock_fallback`, and `Tenant_name_updates_after_reload`. Assert header text beside the logo and document.title are exactly the fetched name, that GET failure leaves WorkerDock and navigation usable, and that a fresh render shows the persisted name. Update fetch fixtures to return /api/settings separately from /api/session.
- [ ] **Step 2: Run red.** `npm.cmd --prefix frontend test -- --run apps/tenant/src/auth/SessionProvider.test.tsx apps/tenant/src/features/settings/useTenantSettings.test.tsx`; expect new branding tests to fail.
- [ ] **Step 3: Implement shared state and branding.** Load the settings snapshot in the hook, call it in App only for readers, set document.title from the snapshot with WorkerDock fallback, and pass the same name to AppShell. Keep the brand mark and sign-in/out behavior.
- [ ] **Step 4: Run green.** Run Task 2 command; expect pass.
- [ ] **Step 5: Commit.** Stage Task 2 files, run `git diff --cached --check`, commit `feat: show tenant display name in header and tab`.

### Task 3: Render grouped navigation without changing routes

**Files:** Modify frontend/apps/tenant/src/App.tsx, frontend/packages/ui/src/AppShell.tsx, frontend/packages/ui/locales/en.json, frontend/packages/ui/locales/pt-BR.json, and frontend/apps/tenant/src/auth/SessionProvider.test.tsx. Create frontend/packages/ui/src/AppShell.test.tsx for focused shell coverage.

**Interfaces:** Export `NavigationGroup = { id: string; label: string; items: { id: string; label: string }[] }` from AppShell or the UI package index. App passes groups in the exact Monitoring, Maintenance, Administration order. Add locale keys navMonitoring, navMaintenance, navAdministration.

- [ ] **Step 1: Write failing tests.** Name tests `Navigation_groups_match_permissions_and_order` and `Deep_links_select_existing_navigation_item`. Assert translated group headings, ordered links, manager-only Jobs, reader-visible Settings, active Documentation for help:<slug>, active Job monitoring for execution:<id>, and unchanged deep-link hashes.
- [ ] **Step 2: Run red.** `npm.cmd --prefix frontend test -- --run apps/tenant/src/auth/SessionProvider.test.tsx packages/ui/src/AppShell.test.tsx`; expect group assertions to fail.
- [ ] **Step 3: Render groups.** Replace the flat array with typed groups; render each heading and its links in one nav landmark. Preserve href, aria-current, skip link, keyboard behavior, and responsive wrapping. Derive page title and contextual help from the flattened items.
- [ ] **Step 4: Run green.** Run Task 3 command; expect pass in both locales.
- [ ] **Step 5: Commit.** Stage Task 3 files, run `git diff --cached --check`, commit `feat: group tenant navigation`.

### Task 4: Move access and preferences into Settings

**Files:** Create frontend/apps/tenant/src/features/settings/PersonalSettings.tsx and TenantSettingsForm.tsx. Modify frontend/apps/tenant/src/App.tsx, frontend/packages/ui/src/AppShell.tsx, frontend/apps/tenant/src/auth/SessionProvider.test.tsx, and locales if new tab labels are needed.

**Interfaces:** PersonalSettings receives `{ t: Labels; mode: Session["mode"]; canManage: boolean; language: Language; setLanguage(value: Language): void; timeZone: string; setTimeZone(value: string): void }`. TenantSettingsForm receives Task 2's current snapshot, reload, and acceptSave callbacks and the existing Labels. App defaults Settings to Personal, exposes management tabs only when canManage, and passes successful PUT data to acceptSave.

- [ ] **Step 1: Write failing tests.** Name tests `Reader_sees_only_personal_settings`, `Anonymous_access_warning_is_only_in_Settings`, `Preferences_persist_and_translate_in_Settings`, `Successful_save_updates_header_and_title_immediately`, and `Failed_or_stale_save_does_not_change_name`. Check absence of repeated access/preferences panels on Dashboard and Documentation, no manager-only controls for Viewer, language/time-zone localStorage behavior, and the 412 response path.
- [ ] **Step 2: Run red.** `npm.cmd --prefix frontend test -- --run apps/tenant/src/auth/SessionProvider.test.tsx`; expect new location and save assertions to fail.
- [ ] **Step 3: Extract and compose Settings views.** Move existing SettingsForm behavior to TenantSettingsForm. Move access panel and bottom preferences card into PersonalSettings. Keep mode in AppShell only where sign-in behavior needs it. Gate management views, not the Settings route. On successful PUT call acceptSave; on failed PUT preserve the app-wide snapshot and draft error.
- [ ] **Step 4: Run green.** Run Task 4 command; expect pass.
- [ ] **Step 5: Commit.** Stage Task 4 files, run `git diff --cached --check`, commit `feat: centralize personal settings`.

### Task 5: Update guidance and verify responsive behavior

**Files:** Modify frontend/apps/tenant/src/help/content/en/settings.md, pt-BR/settings.md, en/getting-started.md, pt-BR/getting-started.md, and any focused documentation content tests. Add frontend/e2e/tenant-navigation.spec.ts for responsive navigation and display-name behavior if the existing e2e fixture can serve the tenant UI.

**Interfaces:** Help says Read users can open Personal Settings, Manage users can edit tenant configuration, preferences live only under Settings, and the chosen display name appears in the header and browser tab.

- [ ] **Step 1: Write failing content and responsive checks.** Assert bilingual guidance names Personal Settings and correct role boundary. At a narrow viewport, mock /api/settings with a long display name and assert header/name/sign-in controls remain visible without horizontal overflow; verify all three navigation groups can be used.
- [ ] **Step 2: Run red.** `npm.cmd --prefix frontend test -- --run apps/tenant/src/help/content-admin.test.ts` and `npm.cmd --prefix frontend run e2e -- tenant-navigation.spec.ts`; expect new assertions to fail.
- [ ] **Step 3: Update help and layout.** Replace outdated “preferences in the header” and “Settings requires Manage” text. Adjust responsive header layout only if the e2e check shows overlap or overflow.
- [ ] **Step 4: Run green and final gates.** Run Task 5 commands, `npm.cmd --prefix frontend run build`, and `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~AccessPolicyTests`. Record any unavailable browser prerequisite as blocked rather than passing.
- [ ] **Step 5: Commit.** Stage Task 5 files, run `git diff --cached --check`, commit `docs: update tenant settings and navigation guidance`.
