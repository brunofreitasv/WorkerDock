# Tenant help center and alert creation

## Purpose and success

Tenant users need instructions inside the WorkerDock web app for the workflows they can perform there. A reader should be able to find an English or Brazilian Portuguese guide, follow steps that match the installed release, and return to the relevant screen. Managers should also be able to create webhook sinks and alert rules in the Alerts screen, completing the alert setup workflow without an API client.

Developer-authored templates remain a source-code task. The tenant guide explains that distinction and points developers to `docs/how-to/add-custom-template.md` in the source repository. It does not offer template package creation or upload in the tenant app.

## Scope

The first release covers the current tenant areas: dashboard; jobs and execution monitoring; event timelines; workers and audit; incidents and deliveries; alert setup; SQL Server profiles; installed templates; job creation and lifecycle actions; schedules and execution policies; SQL Server and HTTP job configuration; general settings; outbound policy; vault references; retention; and local preferences. Each task guide states prerequisites, required role, steps using current UI labels, expected result, and a relevant in-app destination. English and Portuguese topic coverage is identical.

The work adds alert sink and rule creation to the existing Alerts page. Existing editing and read-only monitoring behavior remains. The work does not add backend alert contracts, new alert conditions or sink types, a template authoring UI, a documentation CMS, or deployment administration screens.

## Help architecture

Add a Help item to the tenant navigation for sessions with Read access. A topic index provides concise task names, categories, and client-side text search. Each topic has a stable, validated hash route so users can bookmark or share it. A contextual Help link beside the page heading opens the matching topic for the current tenant page, where available. Switching language retains the topic. Unknown topic routes show a localized topic-not-found state with a link to the index.

Guides live as versioned Markdown files under `frontend/apps/tenant/src/help/content/en` and `frontend/apps/tenant/src/help/content/pt-BR`. A single topic manifest defines slugs, order, category, and related tenant route. Vite bundles both language sets with the tenant app; no live documentation service, API endpoint, or external network request is needed. A Markdown renderer supports headings, lists, links, tables, and code blocks, with raw HTML disabled. In-app destinations use explicit route mappings. External or source-repository references are displayed as references rather than assuming a repository URL exists in deployment.

The Help page is readable by users with Read access. Guides mark steps that require Manage permission and do not expose hidden management controls. The content contains no tenant-specific values or secrets. UI labels and navigation labels are localized through the existing English and Portuguese resources.

## Guide inventory

1. Getting started, roles, navigation, and language preferences.
2. Dashboard: counts, capacity, refresh timestamps, upcoming work, and recent executions.
3. Monitor jobs, execution details, status timeline, safe event codes, filtering, and stale data.
4. Worker status and audit events.
5. Alert setup: outbound policy and vault-reference prerequisites, webhook sink creation, rule creation and activation, incidents, deliveries, and retry behavior.
6. SQL Server profiles: server, allowlisted catalogs, authentication, encryption, diagnostic, revision, and retirement.
7. Installed templates: browse descriptions and versions, select a template, understand retired versions, and the source-doc pointer for developer creation.
8. Create and manage jobs: wizard stages, validation, disabled draft, activation, run now, duplicate, pause, resume, disable, archive, and stop.
9. Schedules and execution policies: manual versus scheduled jobs, preview, time zones, retries, overlap, missed work, and timeouts.
10. SQL Server jobs: profile and catalog, query or procedure, typed parameters, secret references, and execution-key use.
11. HTTP jobs: method and destination, outbound allowlist, headers and secret headers, substitution, success statuses, response mode, and timeout.
12. Settings: general tenant settings, outbound policy, vault references, retention, and local display preferences.

Guides explain observable behavior and safe configuration steps. Existing `docs/reference` and `docs/how-to` remain the deeper source-code and deployment references; guides should not claim that memory-mode behavior proves live SQL, delivery, or production readiness.

## Alert creation behavior and data flow

Managers see “New webhook sink” and “New alert rule” actions in the current Alerts page. A new sink form starts with a generated UUID, webhook type, safe numeric defaults, an empty URL and header list, and no secret values. The new sink defaults to enabled and may be disabled before saving; a sink alone sends nothing until an enabled rule selects it. The form accepts only header names and logical secret references. The existing outbound policy and secret-provider requirements are explained beside the form and in Help.

A new rule form starts with a generated UUID and `enabled: false`. It requires an enabled sink, a supported condition, and valid threshold, window, grace, and reminder values. The first save creates a disabled rule; the Enable control becomes available after that save, so activation requires a separate explicit action. The create action sends the same validated body shape as edit to `PUT /api/alert-sinks/{id}` or `PUT /api/alert-rules/{id}`, without `If-Match`. The server already treats a previously absent ID with no expected version as creation and returns the saved version. After success, the UI selects the saved item and reads its ETag from a GET request before offering the existing edit controls. Edits continue to send `If-Match`.

Keep the new item ID and form values when validation, network, or persistence fails. A repeated create with an ID that was saved despite an uncertain response can return a stale-version error; in that case, fetch that ID and present the saved item for review instead of generating a second item. Switching between new and existing items with unsaved changes opens a discard confirmation; Cancel keeps the current draft and selection. Reader sessions request only incident and delivery resources and never render creation or editing controls.

## Errors, safety, and accessibility

Show field-level validation where possible and a clear summary for save failures. Preserve the draft on all failures. Do not expose webhook secret values in state, payloads, messages, or rendered help. Keep the existing same-origin, CSRF, Manage authorization, and server-side validation path. Report unavailable or stale data as such. Use semantic headings, labeled inputs, keyboard-operable search and navigation, visible focus, and announced error/success messages.

## Verification and acceptance

- Unit tests confirm topic-manifest parity between languages, valid internal links, safe rendering, search, direct topic navigation, language switching, and unknown-topic behavior.
- Alert UI tests cover sink creation, rule creation, default disabled rule, client and server validation, post-create ETag editing, uncertain-save recovery, preserved drafts, secret-reference-only payloads, and reader permissions.
- Browser tests cover an English and Portuguese help journey and a manager alert-setup journey with mocked API responses. Existing backend alert tests continue to cover persistence and validation; no new backend route is planned.
- Run the tenant typecheck/build and focused frontend tests. Review the guides against the current screens and source reference docs before shipping.

The work is complete when a Read user can open every listed guide in either language from the tenant app, contextual destinations work, a manager can create a sink and disabled rule through Alerts and then enable the rule, and readers retain read-only alert views.
