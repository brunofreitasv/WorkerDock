# WorkerDock web UX refresh

Date: 2026-10-29

## Intent

Improve the tenant app and Control Center as one calm, spacious operations UI. The recent history work added useful data, but filters, actions, and tables sit too close together. Make each screen easier to scan and use while preserving its behavior.

## Decisions from the conversation

- Calm, spacious visual direction.
- Shared system plus a page-by-page pass across both web apps.
- Proceed from this description without separate visual mockups.
- Use the existing React, MUI, and Vite stack and the Sol model.

## Goals

- Make page purpose, primary action, filters, and results obvious.
- Separate period and status filters from refresh actions and data tables.
- Keep dense operational data readable; emphasize open items and latest activity.
- Unify navigation, forms, states, spacing, and tables across both apps and screen sizes.
- Preserve the Dashboard-to-history drilldown, both locales, and Read/Manage access.

## Non-goals and anti-goals

- No backend or data-model redesign, new business actions, retention changes, or job-permission changes.
- Do not bury useful data behind extra clicks or replace text labels with icons alone.
- Do not rely on color alone for status.
- Do not leave the rest of the app inconsistent after polishing the recent history screens.

## Existing system and constraints

- Tenant AppShell has a sidebar, but the page title and contextual help are loose elements. Mobile renders the full navigation above content.
- The tenant theme lives in App.tsx; Control Center uses a separate header without that theme.
- PeriodControl always displays date fields and its presets lack a strong selected state. History pages assemble their own crowded rows of search, status, Refresh, and updated time.
- Dashboard uses cards; alerts stack incident history, delivery history, and configuration; settings has a row of buttons as tabs.
- The tenant UI supports English and pt-BR. Permission checks distinguish Read and Manage. Existing uncommitted job-management work in this worktree must be preserved and included.

## Approaches

1. Theme and component polish only: quick and low disruption, but crowded page composition and mobile navigation remain.
2. Shared system plus every-page layout pass (selected): reusable visual rules and page pieces applied across the app. This addresses the request and keeps established workflows.
3. Full navigation rewrite: maximum freedom but increased risk of disrupting familiar operator paths and behavior.

## Design

### Shared system

Export one MUI theme from the UI package for both apps. Use restrained teal accents, neutral surfaces, high-contrast text, consistent spacing, clear borders, modest radius, and visible focus. Keep table rows efficient inside spacious pages. Use existing MUI; add no visual dependency.

Introduce reusable page heading, section surface, filter panel, results toolbar, empty state, status badge, and responsive action grouping. They establish layout and accessibility while allowing each page its own fields and copy.

### Shell

Retain tenant routes and navigation groups. Refine desktop sidebar grouping and active state. On mobile, use a compact menu and dismissible drawer so page content starts near the top. Put title, useful context, and contextual help together in a page header. Keep keyboard skip navigation. Align the Control Center header, width, and surfaces to the same system without adding a sidebar.

### Filter and results pattern

Place filters in a labeled surface above a separate results surface. Show period presets with clear active states. Show custom start/end dates only after Custom is chosen; incomplete or invalid dates keep the previous applied range and show inline guidance. Other filters use a responsive grid. Clear resets to the page default. Refresh and last-updated text live in the results toolbar. Applied filter changes reset cursor pagination.

Preserve query semantics, including older open incidents in the default active view and open/latest-activity ordering. Use readable status badges, date and duration formatting, quiet row separators, hover/focus states, and useful empty states. Wide tables scroll horizontally on narrow screens with a visible cue. Put pagination in the results footer.

### Screen application

- Dashboard: current health and open items first; then scannable metrics, recent executions, and upcoming work in separate sections. Keep the full-history link prominent.
- Monitoring, workers, execution details: group identity, state, diagnostics, and actions; distinguish read-only details from Manager controls.
- Execution history, job history, alerts, audit: the shared filter/results structure with their specific fields and columns.
- Alerts: incident activity first, delivery history second, Manager configuration in a distinct administration section. Divide long forms into labeled subsections.
- Jobs, templates, settings, help: consistent page headers, sectioning, forms, actions, and loading/empty/error states. Replace the settings button row with accessible responsive tabs. Keep job editing separate from history.
- Control Center: align branding/theme, separate directory search from actions, clarify health and row actions.

### Accessibility and responsive behavior

Stack page actions and filter fields at narrow widths; keep controls from clipping. Give wide tables readable minimum widths and horizontal scroll. Use semantic headings, labeled inputs, visible selection/focus, contrast, keyboard operation, and reduced-motion-friendly transitions. Supply English and pt-BR copy for new tenant text.

## Validation

Run focused component tests for shared layout, filter selection/custom dates/reset, and permission-sensitive controls. Build both apps and run the full frontend suite serially because parallel runs have known timeouts. Inspect Dashboard, history, alerts, audit, settings, and Control Center at desktop and narrow widths and fix visual/keyboard issues found. Preserve unrelated working-tree changes and stage only UX files.

## Builder's calls

Choose exact tokens, component boundaries, and visual details within the selected direction. Decide after inspection which dense tables need mobile cards versus horizontal scroll. Keep copy concise and use existing localization patterns.
