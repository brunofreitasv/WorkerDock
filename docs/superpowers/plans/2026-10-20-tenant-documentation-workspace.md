# Tenant Documentation Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Replace the shallow tenant Help page with a polished, searchable, bilingual Documentation workspace containing 24 useful guides and visual explanations.

**Architecture:** Keep documentation bundled in the tenant frontend. A typed topic manifest drives grouped browsing, route resolution, related guides, and client-side search; separate home, article, outline, and safe Markdown/media units render it inside the existing app shell. Existing help hash routes stay valid, including aliases for removed broad guides.

**Tech Stack:** React 19, TypeScript 7, Material UI 9, Vite 8, react-markdown, remark-gfm, Vitest, Testing Library, Playwright.

**Spec:** docs/superpowers/specs/2026-10-20-tenant-documentation-workspace-design.md

## Global Constraints

- Visible English name: Documentation. Visible Brazilian Portuguese name: Documentação.
- 24 canonical guides in five groups, in both en and pt-BR, with the exact slugs in the spec.
- Preserve #/help, #/help:<slug>, contextual links, and old workers-audit and alerts slugs as aliases.
- No tenant API, database, job, alert, or permission behavior changes.
- Markdown raw HTML stays disabled; links and images use only approved internal routes and bundled assets.
- Use synthetic examples and screenshot fixtures; no secrets, tenant identifiers, or credentials.
- Prefer diagrams to screenshots; every visual needs alt text, caption, and prose explanation.
- Existing source docs remain authoritative for developer template authoring and deployment details.

## Review Focus

1. Multiword and accent-varied searches must return all-term matches, with title matches ranked before body matches; pin in Task 1 tests.
2. Old aliases, malformed slugs, and language changes must preserve useful navigation; pin in Tasks 1 and 4 tests.
3. Unknown image paths and unsafe Markdown URLs must never fetch or navigate externally; pin in Task 2 tests.
4. A Read user may open the read-only Alerts screen but cannot follow manager-only Jobs or Settings actions; pin in Tasks 2 and 4 tests.
5. Duplicate or accented article headings and long Portuguese labels must keep outline anchors unique and mobile navigation usable; pin in Tasks 2 and 10 tests.

## File map

- frontend/apps/tenant/src/help/topics.ts: manifest, group metadata, aliases, and topic resolution.
- frontend/apps/tenant/src/help/search.ts: normalized ranking and safe result excerpts.
- frontend/apps/tenant/src/help/content.ts: localized Markdown lookup through canonical slugs.
- frontend/apps/tenant/src/help/articleOutline.ts: stable heading IDs and outline data.
- frontend/apps/tenant/src/help/media.ts and media/: allowlisted bundled SVG/PNG assets.
- frontend/apps/tenant/src/help/HelpMarkdown.tsx: safe links, media, and heading IDs.
- frontend/apps/tenant/src/help/DocumentationHome.tsx: guided hub and search results.
- frontend/apps/tenant/src/help/DocumentationArticle.tsx: breadcrumb, topic rail, article, outline, related guides.
- frontend/apps/tenant/src/help/HelpPage.tsx: route-level home/article/recovery selection.
- frontend/apps/tenant/src/help/documentationCopy.ts: local UI copy in both languages.
- frontend/apps/tenant/src/help/content/{en,pt-BR}/*.md: 24 guide pairs.
- frontend/apps/tenant/src/App.tsx and frontend/packages/ui/locales/{en,pt-BR}.json: visible navigation and contextual labels.
- frontend/apps/tenant/src/help/*.test.ts(x), frontend/apps/tenant/src/auth/SessionProvider.test.tsx, and frontend/e2e/help.spec.ts: unit, integration, and browser checks.

---

### Task 1: Topic manifest, aliases, and search

**Files:** Modify topics.ts and content.ts; create search.ts, topics.test.ts, search.test.ts.

**Interfaces:** Export HelpGroupId, HelpTopic, helpGroups, helpTopics, resolveHelpTopic(slug: string | undefined): HelpTopic | undefined from topics.ts. HelpTopic has slug, group, localized title/summary/keywords, route, requiredRole, and related slugs. Export SearchResult with topic, excerpt, and matchField; export searchHelpTopics(language: Language, query: string): SearchResult[] from search.ts. getHelpArticle(language, slug) resolves aliases before loading the Markdown.

- [ ] **Step 1: Write failing manifest tests.** Assert the 24 exact canonical slugs and five group orders from the spec, unique IDs, complete bilingual metadata, valid related slugs, old workers-audit -> workers and alerts -> alert-rules aliases, and undefined for malformed slugs.
- [ ] **Step 2: Write failing search tests.** Assert a two-term query spanning title/body matches both terms, Portuguese accent-insensitive matching, title before body ranking, plain-text excerpt without Markdown syntax, and empty query returning no search results.
- [ ] **Step 3: Run focused tests.** From frontend run npx vitest run apps/tenant/src/help/topics.test.ts apps/tenant/src/help/search.test.ts; expect red failures for the new contracts.
- [ ] **Step 4: Implement the manifest, resolver, localized content lookup, and search.** Use normalization with Unicode NFD and combining-mark removal, all-term matching, deterministic field weights title > keywords > summary > body, then manifest order. Excerpt length is at most 160 characters around the strongest body match, otherwise the summary.
- [ ] **Step 5: Rerun the focused tests; expect pass. Commit only Task 1 files.**

### Task 2: Safe article rendering and outline data

**Files:** Modify HelpMarkdown.tsx; create articleOutline.ts, media.ts, articleOutline.test.ts, HelpMarkdown.test.tsx.

**Interfaces:** Export ArticleHeading = { level: 2 | 3; id: string; title: string } and parseArticleHeadings(source: string): ArticleHeading[] from articleOutline.ts. Markdown headings are plain text H2/H3 lines; normalized duplicate IDs receive numeric suffixes. Export getHelpMedia(src: string): string | undefined from media.ts, accepting only registered ./media/<name>.svg or .png assets. HelpMarkdown accepts source, headings, canManage, and onNavigate.

- [ ] **Step 1: Write failing heading tests.** Assert accented heading IDs, duplicate suffixes, and stable H2/H3 order.
- [ ] **Step 2: Write failing renderer tests.** Assert heading IDs match the outline, raw HTML is text, javascript and unknown hash links stay inert, unknown/remote images produce alt text without a request, and Read users can follow #/alerts but not #/jobs or #/settings.
- [ ] **Step 3: Run npx vitest run apps/tenant/src/help/articleOutline.test.ts apps/tenant/src/help/HelpMarkdown.test.tsx from frontend; expect failures.**
- [ ] **Step 4: Implement the parser and renderer.** Keep react-markdown with skipHtml and remark-gfm, render only mapped routes and local media, and use the same parsed headings for IDs and outline links.
- [ ] **Step 5: Rerun the focused tests; expect pass. Commit only Task 2 files.**

### Task 3: Guided Documentation home

**Files:** Create DocumentationHome.tsx and documentationCopy.ts; modify HelpPage.tsx, App.tsx, frontend/packages/ui/locales/en.json, frontend/packages/ui/locales/pt-BR.json; modify HelpPage.test.tsx.

**Interfaces:** DocumentationHome({ language, canManage, onNavigate }): ReactElement reads helpGroups/helpTopics and searchHelpTopics. HelpPage retains its existing props and renders the home when slug is absent. Keep App route IDs as help; change only visible copy.

- [ ] **Step 1: Replace the obsolete exact-12 and old article assertions with failing home tests; Task 4 restores article coverage.** Assert Documentation/Documentação titles; shortcuts for first job, monitor execution, SQL Server job, and alert rule; five ordered group sections; 24 topic cards; accessible labeled search; ranked result group/excerpt; clear/reset action; useful no-result view; and visible Manage badges.
- [ ] **Step 2: Run npx vitest run apps/tenant/src/help/HelpPage.test.tsx from frontend; expect failures.**
- [ ] **Step 3: Implement the MUI home and labels.** Use a bounded content width, clear type scale and spacing, compact task cards, a prominent search control, and normal anchor/button keyboard behavior. Update contextual copy to “Documentation for this page” and “Documentação desta página”.
- [ ] **Step 4: Rerun the focused test and tenant build; expect pass. Commit only Task 3 files.**

### Task 4: Article workspace and recovery view

**Files:** Create DocumentationArticle.tsx and DocumentationArticle.test.tsx; modify HelpPage.tsx and HelpPage.test.tsx.

**Interfaces:** DocumentationArticle({ language, topic, source, canManage, onNavigate }): ReactElement uses parseArticleHeadings, HelpMarkdown, and the manifest. HelpPage resolves aliases before loading an article and shows localized grouped recovery for missing slugs/content.

- [ ] **Step 1: Write failing article tests.** Assert breadcrumb home/group, active sibling topic, H2/H3 outline links, related/next guides, role-aware Open screen action, and no “All help topics” button. Assert legacy aliases render canonical content and malformed slugs recover with search.
- [ ] **Step 2: Run npx vitest run apps/tenant/src/help/DocumentationArticle.test.tsx apps/tenant/src/help/HelpPage.test.tsx from frontend; expect failures.**
- [ ] **Step 3: Implement the three-column article layout.** Use MUI responsive grid; collapse topic rail and outline into accessible controls below the large breakpoint. Keep the global app shell and help hash routes. Reader links to #/alerts remain available; manager-only destinations do not.
- [ ] **Step 4: Rerun focused tests and tenant build; expect pass. Commit only Task 4 files.**

### Task 5: Start here and Operate and monitor guides

**Files:** Modify existing getting-started.md, dashboard.md, monitoring.md in both locales; create first-job.md, execution-events.md, workers.md, audit.md in both locales; delete old workers-audit.md pairs; create contentCoverage.test.ts.

**Interfaces:** Use H2 headings What you will do, Before you start, Steps (or Explore the screen for overviews), Example, Check the result, If it does not work, and Related guides; Portuguese equivalents are O que você vai fazer, Antes de começar, Etapas (or Explore a tela), Exemplo, Verifique o resultado, Se não funcionar, and Guias relacionados. References use known tenant hash routes and canonical guide slugs.

- [ ] **Step 1: Write failing content tests for these seven slugs.** Assert both languages exist, required task sections or overview equivalents exist, and every internal link resolves. Include role/availability checks for worker, audit, and stale-data guidance.
- [ ] **Step 2: Run npx vitest run apps/tenant/src/help/contentCoverage.test.ts from frontend; expect failures.**
- [ ] **Step 3: Write the seven bilingual guides.** Verify UI labels and behavior against Dashboard, JobsTable, ExecutionDetails, WorkersPage, AuditPage, docs/reference/monitoring.md, and existing source guidance. Include synthetic status/timeline examples and how to recognize stale or unavailable data.
- [ ] **Step 4: Rerun the content test; expect pass. Read both languages once for equivalent coverage. Commit Task 5 files.**

### Task 6: Build and run jobs guides

**Files:** Modify templates.md, jobs.md, sql-jobs.md, http-jobs.md, schedules.md in both locales; create job-inputs.md, manual-runs.md, execution-policies.md in both locales; extend contentCoverage.test.ts.

**Interfaces:** Eight guide pairs use canonical slugs and the content contract from Task 5.

- [ ] **Step 1: Extend the failing coverage test for all eight guide pairs.** Assert links and prerequisite/example/verification/recovery sections; specifically verify the installed-template guide points to docs/how-to/add-custom-template.md as a source reference, not a tenant creation link.
- [ ] **Step 2: Run the focused content test; expect failures.**
- [ ] **Step 3: Write the bilingual guides.** Check JobWizard, ManualJobEditor, template picker, ScheduleEditor, docs/how-to/manage-jobs.md, SQL/HTTP template references, and scheduling docs. Cover disabled drafts, activation, safe SQL/HTTP examples, parameter modes, dynamic preview limits, time zones, run-now override, retries, overlap, and lifecycle actions.
- [ ] **Step 4: Rerun content tests; expect pass. Read both languages for equivalent coverage. Commit Task 6 files.**

### Task 7: Integrations and alerts guides

**Files:** Modify sql-profiles.md in both locales; create outbound-policy.md, secrets.md, webhook-sinks.md, alert-rules.md, incidents-deliveries.md in both locales; delete old alerts.md pairs; extend contentCoverage.test.ts.

**Interfaces:** Six guide pairs use canonical slugs and the same content contract.

- [ ] **Step 1: Extend the failing coverage test for these six guide pairs and valid links.** Assert secret references are explained without inline secret values, alert rule first save is disabled, and delivery retry/deduplication guidance is present.
- [ ] **Step 2: Run the focused content test; expect failures.**
- [ ] **Step 3: Write the bilingual guides.** Check ProfileEditor, VaultSettingsPage, OutboundPolicyPage, AlertRulesPage, docs/reference/alerts.md and outbound/secrets references. Cover profile diagnostics, host and port policy, sink setup, rule conditions/activation, incident review, delivery results, and at-least-once delivery.
- [ ] **Step 4: Rerun content tests; expect pass. Read both languages for equivalent coverage. Commit Task 7 files.**

### Task 8: Administration guides and full content validation

**Files:** Modify settings.md in both locales; create retention.md and troubleshooting.md in both locales; extend contentCoverage.test.ts.

**Interfaces:** Three guide pairs complete the 24-topic corpus.

- [ ] **Step 1: Extend the failing content test to all 24 topics.** Assert both locale files exist, every internal guide/tenant link and related slug resolves, and no article references a deleted broad guide except supported alias tests.
- [ ] **Step 2: Run the focused content test; expect failures.**
- [ ] **Step 3: Write the three bilingual guides.** Verify current settings, retention/archive controls, permission messages, stale data, and common job/alert failure recovery against the tenant UI and source references.
- [ ] **Step 4: Rerun content tests; expect pass. Review both languages for accuracy and equivalent coverage. Commit Task 8 files.**

### Task 9: Diagrams and selective annotated screenshots

**Files:** Create local SVG diagrams and PNG screenshots under frontend/apps/tenant/src/help/media/, media/README.md, and frontend/scripts/capture-documentation.mjs; modify relevant guide pairs to reference them; extend contentCoverage.test.ts.

**Interfaces:** Markdown uses ./media/<asset>.svg or .png, resolved by getHelpMedia. Each image has meaningful alt text, a visible caption, and adjacent prose.

- [ ] **Step 1: Add failing media-reference tests.** Assert every referenced asset resolves locally, has alt text and a following italic Figure:/Figura: caption, and no article embeds a remote image.
- [ ] **Step 2: Run the focused content and renderer tests; expect failures.**
- [ ] **Step 3: Create at least five simple SVG flow/decision diagrams.** Cover first job, execution lifecycle, SQL/HTTP input choice, scheduling, and alert delivery. Make text legible on narrow screens.
- [ ] **Step 4: Implement capture-documentation.mjs.** Use Playwright routes and synthetic fixtures patterned after existing e2e job, monitoring, and alert specs; target the job wizard, execution timeline, and alert setup.
- [ ] **Step 5: Run the capture script against the local tenant preview.** Save at most three real UI screenshots, with no tenant identifiers or secrets.
- [ ] **Step 6: Annotate numbered controls and document the exact preview/capture commands in media/README.md.**
- [ ] **Step 7: Add localized visual references, captions, and prose explanations.**
- [ ] **Step 8: Run the media/content tests and tenant build; expect pass. Visually inspect each asset at desktop and narrow widths.**
- [ ] **Step 9: Commit Task 9 files.**

### Task 10: Browser journeys and final verification

**Files:** Modify frontend/e2e/help.spec.ts and frontend/apps/tenant/src/auth/SessionProvider.test.tsx; modify Documentation components only for issues revealed by tests.

**Interfaces:** Existing hash routes and session fixtures remain the browser entry points.

- [ ] **Step 1: Write browser tests for English and Portuguese home/article journeys, search rank and no-result reset, contextual links, legacy aliases, language-preserving deep links, reader role behavior, no external documentation fetches, and outline navigation.** Use a narrow viewport case with long Portuguese labels and keyboard navigation.
- [ ] **Step 2: Run npx playwright test e2e/help.spec.ts --project=chromium from frontend; record any failures as concrete defects for Step 3. A pass is valid if the earlier tasks already satisfy the journeys.**
- [ ] **Step 3: Correct only observed layout, behavior, or content defects.** Review screenshots at desktop and narrow widths for hierarchy, clipping, callout legibility, and focus states.
- [ ] **Step 4: Run npx vitest run apps/tenant/src/help apps/tenant/src/auth/SessionProvider.test.tsx from frontend; expect pass.**
- [ ] **Step 5: Run npm run build --workspace @workerdock/tenant from frontend; expect pass.**
- [ ] **Step 6: Run npx playwright test e2e/help.spec.ts --project=chromium from frontend; expect pass.**
- [ ] **Step 7: Check git diff --check and review the final diff against the spec.**
- [ ] **Step 8: Commit Task 10 changes.**
