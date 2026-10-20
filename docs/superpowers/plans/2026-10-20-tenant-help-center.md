# Tenant Help Center Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every current tenant workflow has an English and Brazilian Portuguese task guide reachable inside the tenant app.

**Architecture:** Bundle authored Markdown with the Vite tenant build, index it through a typed topic manifest, and render it safely in a Help page using existing hash navigation. Complete the alert-creation UI plan first so the alert guide matches the actual screen.

**Tech Stack:** React 19, TypeScript 7, MUI 9, Vite 8, `react-markdown` with `remark-gfm`, Vitest/Testing Library, Playwright.

**Spec:** `docs/superpowers/specs/2026-10-20-tenant-help-center-and-alert-creation-design.md`

## Global Constraints

- Show Help to Read sessions; mark Manage steps and prevent Read users from following manager-only destinations.
- Bundle English and `pt-BR` files with the app; no docs API, runtime network fetch, CMS, or raw HTML rendering.
- Keep the same topic slugs in both languages; topic links remain stable across language changes.
- Use current UI labels and distinguish memory-mode examples from live SQL/delivery behavior.
- Template creation is only a pointer to `docs/how-to/add-custom-template.md` in the source repository; no assumed public repository URL or template upload UI.
- Stage and commit only this plan's files; other work exists in the shared worktree.

## Review Focus

- `#/help:unknown` or a malformed hash shows a localized not-found/index path, never a blank page (Task 2 test).
- Raw HTML and `javascript:`/unlisted Markdown links never become executable or navigable (Task 1 test).
- An accent/case variation in a Portuguese search term still finds its guide (Task 1 test).
- Switching locale while reading a deep-linked topic keeps the slug and loads that locale's body (Task 2 test).
- A Read user can read manager instructions but cannot navigate through a manager-only guide link (Task 2 test).

## File structure

- Create `frontend/apps/tenant/src/help/topics.ts`: localized metadata, role, category, slug, destination, search.
- Create `frontend/apps/tenant/src/help/content.ts`: compile-time Markdown imports and article lookup.
- Create `frontend/apps/tenant/src/help/HelpMarkdown.tsx`: safe Markdown rendering and validated links.
- Create `frontend/apps/tenant/src/help/HelpPage.tsx`: index, search, article layout, role badges, not-found state.
- Create `frontend/apps/tenant/src/help/HelpPage.test.tsx`: manifest, renderer, search, navigation, and locale tests.
- Create Markdown pairs in `frontend/apps/tenant/src/help/content/{en,pt-BR}/` for `getting-started`, `dashboard`, `monitoring`, `workers-audit`, `alerts`, `sql-profiles`, `templates`, `jobs`, `schedules`, `sql-jobs`, `http-jobs`, and `settings`.
- Modify `frontend/apps/tenant/src/App.tsx`: Help route, sidebar item, contextual link, title, active navigation.
- Modify `frontend/packages/ui/locales/en.json` and `pt-BR.json`: Help UI labels.
- Modify `frontend/apps/tenant/package.json` and `frontend/package-lock.json`: Markdown renderer dependencies.
- Create `frontend/e2e/help.spec.ts`: bilingual browser journeys.

### Task 1: Render and search bundled guides safely

**Interfaces:** `HelpTopic = { slug: string; title: Record<Language,string>; category: Record<Language,string>; route: string; requiredRole: "Read" | "Manage" }`; export `helpTopics: readonly HelpTopic[]`, `getHelpArticle(language: Language, slug: string): string | undefined`, `searchHelpTopics(language: Language, query: string): HelpTopic[]`, `HelpMarkdown({ source, canManage, onNavigate }: { source: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement`, and `HelpPage({ language, slug, canManage, onNavigate }: { language: Language; slug?: string; canManage: boolean; onNavigate: (route: string) => void }): ReactElement`.

- [ ] **Step 1: Write failing `HelpPage.test.tsx` tests.** Assert the index shows `getting-started` and `dashboard` in both languages; search matches title/body case-insensitively and matches Portuguese text without accents; `getHelpArticle` returns both bodies; rendering crafted `<img onerror=...>` through `HelpMarkdown` is text/no image, and a `javascript:` or unlisted hash link is non-navigable.
- [ ] **Step 2: Run `npx vitest run apps/tenant/src/help/HelpPage.test.tsx` from `frontend`.** Expected: FAIL because the Help module does not exist.
- [ ] **Step 3: Implement.** Add `react-markdown` and `remark-gfm` to the tenant package/lockfile, without raw-HTML plugins. Add `topics.ts`, `content.ts` using eager Vite `?raw` imports, `HelpMarkdown.tsx`, and `HelpPage.tsx`. Normalize search with lowercase and Unicode accent folding. Permit Markdown links only to manifest-backed `#/help:<slug>` or explicit tenant routes, and render disallowed links as text. Author complete `getting-started.md` and `dashboard.md` in both languages, with prerequisites, role, steps, result, and route links; Getting started includes a manager-only `#/jobs` destination for the role test.
- [ ] **Step 4: Run Step 2 and `npm run build --workspace @workerdock/tenant`.** Expected: focused tests and build pass.
- [ ] **Step 5: Commit.** Stage only Task 1 files and commit `feat: render bilingual tenant help guides`.

### Task 2: Integrate Help into tenant navigation

**Interfaces:** Consume Task 1's `HelpPage` and topic manifest. Routes are `#/help` for index and `#/help:<slug>` for articles; `App.tsx` passes its existing `navigate(id: string)` as `onNavigate`.

- [ ] **Step 1: Write failing tests.** Extend `HelpPage.test.tsx` and `frontend/apps/tenant/src/auth/SessionProvider.test.tsx`: Read users see Help, `#/help:dashboard` opens the article with Help selected, unknown/malformed slugs show localized recovery, locale switch keeps the slug and changes the body, and a Read user sees Manage guidance without a navigable `#/jobs` or `#/settings` link. Assert contextual Help from Dashboard opens `help:dashboard` and a malformed `help:` route opens the not-found state.
- [ ] **Step 2: Run `npx vitest run apps/tenant/src/help/HelpPage.test.tsx apps/tenant/src/auth/SessionProvider.test.tsx` from `frontend`.** Expected: FAIL for Help navigation assertions.
- [ ] **Step 3: Implement.** Add `help` navigation/title/route handling in `App.tsx`; normalize `help:<slug>` to `help` for sidebar active state, preserve the exact slug on language changes, and map each current tenant page to its related guide for a contextual Help link, showing that link only when its slug is registered in the manifest. Map dashboard→dashboard, monitoring/execution→monitoring, workers/audit→workers-audit, alerts→alerts, templates→templates, jobs→jobs, Settings/Profiles tab→sql-profiles, and other Settings tabs→settings. Add localized Help, search, not-found, role, and contextual-link labels. Guard manager destinations based on `canManage` in `HelpPage`.
- [ ] **Step 4: Run Step 2 and `npm run build --workspace @workerdock/tenant`.** Expected: focused tests and build pass.
- [ ] **Step 5: Commit.** Stage only Task 2 files and commit `feat: link tenant screens to help`.

### Task 3: Cover monitoring, alerts, profiles, and templates

**Interfaces:** Add five `HelpTopic` entries and Markdown pairs; use the same slug and destination rules from Task 1. Slugs: `monitoring`, `workers-audit`, `alerts`, `sql-profiles`, `templates`.

- [ ] **Step 1: Write failing content assertions in `HelpPage.test.tsx`.** Assert both languages include all five slugs with nonempty localized Prerequisites/Role/Steps/Result sections and valid internal route links; spot-check alert sink → disabled rule → enable, SQL profile diagnostic, execution timeline/stale data, worker/audit views, execution contextual Help → `help:monitoring`, Profiles-tab contextual Help → `help:sql-profiles`, and literal `docs/how-to/add-custom-template.md` developer pointer.
- [ ] **Step 2: Run `npx vitest run apps/tenant/src/help/HelpPage.test.tsx` from `frontend`.** Expected: FAIL for missing topics/content.
- [ ] **Step 3: Author the ten Markdown files and manifest entries.** Cross-check labels and behavior against `Dashboard.tsx`, `JobsTable.tsx`, `ExecutionDetails.tsx`, `WorkersPage.tsx`, `AuditPage.tsx`, `AlertRulesPage.tsx`, `ProfileEditor.tsx`, `TemplatePicker.tsx`, and `docs/reference/{monitoring,alerts,sql-template}.md`. State Manage prerequisites for alert setup/profiles, outbound policy and secret-reference requirements, read-only incident/delivery views, and memory-mode limits.
- [ ] **Step 4: Run Step 2 and `npm run build --workspace @workerdock/tenant`.** Expected: content tests and build pass.
- [ ] **Step 5: Commit.** Stage only Task 3 files and commit `docs: cover tenant monitoring alerts and profiles`.

### Task 4: Complete job/settings guides and browser verification

**Interfaces:** Add the final five `HelpTopic` entries and Markdown pairs. Slugs: `jobs`, `schedules`, `sql-jobs`, `http-jobs`, `settings`. The manifest then has exactly 12 slugs in each locale.

- [ ] **Step 1: Write failing tests.** Assert exactly 12 bilingual topics; each article has prerequisites/role/steps/result and all internal links resolve; spot-check six-stage job wizard, disabled draft/enable, job lifecycle, schedule policies, SQL typed parameters/profile, HTTP destination/secret headers, and settings/retention. In `e2e/help.spec.ts`, assert English direct link/search/contextual navigation and Portuguese locale switch/deep link with no extra documentation fetch.
- [ ] **Step 2: Run `npx vitest run apps/tenant/src/help/HelpPage.test.tsx` and `npx playwright test e2e/help.spec.ts --project=chromium` from `frontend`.** Expected: new tests fail until content and browser flow are complete.
- [ ] **Step 3: Author the ten Markdown files and final manifest entries.** Check `JobWizard.tsx`, `ManualJobEditor.tsx`, `JobActions.tsx`, `ScheduleEditor.tsx`, settings pages, and `docs/how-to/manage-jobs.md` plus `docs/reference/{scheduling,sql-template,http-template,outbound-policy}.md`. Keep every procedural step aligned with the released UI and role model.
- [ ] **Step 4: Re-run Step 2 commands and `npm run build --workspace @workerdock/tenant`.** Expected: focused unit/browser tests and build pass; manually review both language guides against current screens.
- [ ] **Step 5: Commit.** Stage only Task 4 files and commit `docs: complete bilingual tenant help center`.
