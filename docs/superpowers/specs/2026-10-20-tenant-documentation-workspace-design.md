# Tenant Documentation workspace redesign

## Purpose and outcome

Tenant users need an in-app place to learn WorkerDock tasks without reading source code. The current Help page is a plain search box and flat list of twelve short articles. The new Documentation workspace should make common tasks easy to find, teach the full workflow with concrete examples and visual guidance, and match the quality of the tenant UI.

Success means a user with Read access can find a relevant guide from the app navigation or a contextual link, follow accurate English or Brazilian Portuguese instructions for the installed release, and reach the relevant screen. Users with Manage access can follow complete setup guides for jobs, integrations, and alerts. Developer template authoring remains a source-code workflow; the tenant UI only points to docs/how-to/add-custom-template.md in the repository.

## Scope

Replace the presentation, information architecture, search, and content of the tenant Help feature. Rename its visible navigation and page label to Documentation in English and Documentação in Portuguese. Keep the existing hash route family (#/help and #/help:<slug>) and contextual entry points. This project does not change tenant API contracts, job behavior, alert behavior, permissions, or the separate source documentation site.

The released guide set has 24 focused topics in each language, grouped by task:

| Group | Guides |
| --- | --- |
| Start here | Orientation and permissions; first job walkthrough |
| Operate and monitor | Dashboard; execution status; logs and events; worker health; audit history |
| Build and run jobs | Installed templates; job creation; SQL Server jobs; HTTP jobs; job inputs and dynamic values; schedules and time zones; manual runs; execution policies |
| Integrations and alerts | SQL profiles; outbound policy; secrets; webhook sinks; alert rules; incidents and deliveries |
| Administration | Tenant settings; retention and archives; troubleshooting |

Coverage includes the current tenant screens and their important states: stale monitoring data; job drafts, validation, activation, run and lifecycle actions; SQL parameter modes and dynamic value preview; outbound destination restrictions; alert creation and delivery status; and role differences. Explain limits where they matter, including that a UI save or memory-mode result does not prove live SQL execution or webhook delivery. Use synthetic examples and never include tenant data or credentials.

## Experience and navigation

The Documentation home uses the approved guided hub layout. It begins with a clear title and short purpose line, a prominent search field, and links to common tasks. Below, five visually distinct group sections show concise topic cards with title, one-sentence outcome, and role where Manage is required. Cards remain browsable when search is empty. Search results replace the grouped cards only while a query is active. A no-result state offers query reset and grouped browsing.

An article uses the approved topic rail plus page outline layout: breadcrumb at the top, current group and sibling topics in the left rail, readable article column, and an in-page heading outline on the right. The old “All help topics” button is removed. Breadcrumbs lead to Documentation home and the current group; the active topic is visually clear. The article ends with related and next guides and a role-aware “Open screen” action. On narrow screens, the rail and outline become compact, keyboard-operable menus above the article. The global app navigation remains intact.

The visual treatment uses the existing Material UI theme, restrained green accent, generous spacing, clear heading hierarchy, consistent cards, quiet metadata, and visible hover/focus states. Avoid a plain stacked list, unstyled browser controls, oversized search chrome, and decorative elements that compete with the content. Diagrams must remain legible at narrow widths and be available as readable text through captions and adjacent explanation. Respect reduced-motion preferences.

## Topic model, routes, and search

A typed topic manifest is the single source of truth for stable slug, group, ordering, localized title, summary and keywords, required role, target tenant route, and related topics. The 24 canonical slugs are:

| Group | Slugs |
| --- | --- |
| Start here | getting-started, first-job |
| Operate and monitor | dashboard, monitoring, execution-events, workers, audit |
| Build and run jobs | templates, jobs, sql-jobs, http-jobs, job-inputs, schedules, manual-runs, execution-policies |
| Integrations and alerts | sql-profiles, outbound-policy, secrets, webhook-sinks, alert-rules, incidents-deliveries |
| Administration | settings, retention, troubleshooting |

Preserve the already published slugs. Existing canonical slugs above keep their routes. The old workers-audit route resolves to the worker health guide, and the old alerts route resolves to the alert rules guide. Contextual links from Workers, Audit, and Alerts point to their closest new guides. Language switching preserves the current slug. A malformed or unknown slug shows a localized recovery view with search and topic groups.

Search runs entirely in the browser over the current language's bundled title, keywords, summary, category, and article text. Normalize case and accents. Match all query terms, rank title matches above keyword, summary, and body matches, and break ties by manifest order. Each result shows title, group, concise summary, and a short plain-text excerpt from the best match. Highlight matching text as React text nodes, never injected HTML. The search field has a visible label, clear action, and normal keyboard navigation through result links. No network search service or persisted query is needed.

## Guide content and visual system

Localized Markdown article files remain bundled with the tenant release. Every task guide states: what the reader will accomplish; access and prerequisites; numbered steps using the current UI labels; one safe worked example; how to verify success; common mistakes and recovery; and related guides. Overview articles can substitute an annotated screen explanation for artificial numbered steps. Explain role restrictions in the relevant step, not only at the top. Write the two languages for equivalent coverage and examples, while using natural language and localized UI labels.

Use simple diagrams more often than screenshots. Diagrams explain decisions and data movement, especially job setup to activation to execution, schedule timing, SQL versus HTTP job inputs, and alert rule to incident to webhook delivery. Use a few annotated screenshots of the actual tenant UI only for controls that are hard to locate or distinguish in prose, such as the job wizard, SQL profile editor, event timeline, or alert form. Capture screens using synthetic fixture data, remove secrets and tenant identifiers, and add numbered callouts matching the steps. Each visual has alt text, a caption, and a prose equivalent. Diagrams and screenshots are versioned local assets, never fetched from a remote docs host.

The installed-template article shows how to browse and select templates. It states that creating a new template requires a developer change and deployment, and refers to docs/how-to/add-custom-template.md in the source repository. It does not suggest a tenant template creation control. Reference source-doc paths as plain references unless a stable deployed URL is configured.

## Components and data flow

Split the feature into focused units: topic manifest and resolver; localized content loader; search index and ranking; Documentation home; article shell and outline; safe Markdown/media renderer; and localized display copy. The home and article consume the same manifest, so ordering, roles, and links cannot drift. The current tenant App passes language, role, slug, and navigation callbacks to the Documentation page. Existing app routes and API calls are unchanged.

The renderer keeps raw HTML disabled. Only known in-app routes, known topic slugs, and locally registered visual assets become interactive links or images. Unsupported protocols, arbitrary remote images, and unknown routes render as inert text or a safe fallback. Management destinations are unavailable to Read-only users, though the guide may explain the required Manage role. Missing articles or media show a localized recovery state and are also caught by build-time checks. Content must not display secret values, connection strings, request bodies, or tenant-specific records.

## Verification and acceptance

Add focused tests for manifest and article parity across English and Portuguese; valid slugs, aliases, links, target routes, related topics, diagrams, and image references; safe Markdown/media rendering; accent-insensitive ranked search and excerpts; role-aware navigation; unknown-topic recovery; and language switching. Browser checks should cover the home and an article in both languages at desktop and narrow widths, contextual navigation, search with and without results, outline links, and keyboard focus. Review rendered pages visually for hierarchy, clipping, and image legibility.

Review each guide's UI labels, steps, examples, and claims against the current tenant screens and source reference docs. Screenshot fixtures must use synthetic data. The tenant build and focused frontend tests must pass. The work is complete when all 24 guides are available in both languages, users can discover them through the polished Documentation hub and contextual links, the representative workflows are detailed enough to complete and verify, and the legacy help routes still resolve.
