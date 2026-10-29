# Operational history views

Date: 2026-10-29

## Intent

Help operators answer what ran, when, with which template, how long it took, and what needs attention. Make execution, alert, and audit records easier to scan and search over time. The Dashboard remains a quick overview; a separate full execution history supports investigation.

## Decisions from the conversation

- The Dashboard's Recent executions section stays compact and opens a full cross-job execution history.
- Job-specific History remains inside the manager-only Jobs area. Read users can inspect the Dashboard, full execution history, and execution details but cannot modify jobs.
- Job History, Alerts, and Audit use tabular layouts with period and relevant state/type filters and pagination. The reviewed mockups establish the column sets below.
- Audit retention stays configurable with its current 365-day default. Older retained records, including records around six months old, must be searchable.
- The Alerts incident period uses latest activity, and Open incidents appear first.
- Extend the existing APIs separately with server-side filtering and pagination. Do not create a unified event API.

## Goals

- Show useful context in each row without opening every record.
- Keep lists responsive as histories grow by filtering and paging in SQL, never only within the first loaded batch.
- Make time ranges predictable, including older audit periods, and keep Read/Manage permissions consistent with the decisions above.
- Preserve links from list rows to execution details and from the Dashboard to full history.

## Non-goals and anti-goals

- No job editing, run controls, alert retry controls, or audit mutations are added to history tables.
- No merged timeline across executions, incidents, deliveries, and audit events.
- Do not show secrets, raw exception bodies, or private payloads in list responses.
- Do not silently hide active incidents merely because they opened before the default recent period.
- A filter must apply to the whole retained result set, not just rows already in the browser.

## Existing system and constraints

- React/MUI tenant frontend, English and pt-BR labels, ASP.NET endpoints, SQL Server tenant storage, cursor pages in monitoring and job queries.
- `/api/dashboard` currently returns ten recent executions with job name, state, created time, and IDs; it lacks template and duration.
- `/api/jobs/{id}/executions` is a manager-only cursor page with started time and terminal duration but no filters. Its UI currently appends another page rather than presenting page navigation.
- `/api/audit` is a Read-access cursor page, defaults to seven days, supports broad text search, and currently rejects ranges over 90 days.
- Alert incidents and deliveries are Read-access lists capped at 100 records without filters or page cursors. Alert rule and sink editing remains Manage-only.
- Execution retention defaults to 90 days and audit retention to 365 days, both configurable. History searches can only return records still present in tenant storage.
- The default worktree contains extensive unrelated uncommitted work. The implementation must preserve it and stage only files belonging to this feature.

## Approaches

1. **Chosen:** Extend each existing history API with server-side time/type/state filters and cursor pagination, while reusing one frontend pattern for filter bars and previous/next navigation. Clear contracts per record type; moderate changes to several endpoints and query classes.
2. Build one consolidated history API for executions, alerts, and audit. Potential future cross-record search, but more complicated authorization, sorting, schemas, and query performance for the current screens.

## Screen behavior

### Dashboard and full execution history

Keep the ten newest executions and display Job, Template, Status, Duration, and Created in a compact table. Job or row action opens the existing execution details; a visible “View all executions” action opens a new Read-access full history route. Duration is based on the first start and terminal attempt end; show an em dash when it is not yet known. Keep the existing dashboard polling and stale-data message.

The full history table shows Created, Job, Template, Status, Trigger, Started, and Duration, with each row opening execution details. It has preset periods (24 hours, 7 days, 30 days) and custom start/end dates, job-name or exact execution-ID search, template filter, and status filter. Include every persisted execution state in the filter, including Queued, Running, RetryWaiting, NeedsReview, Succeeded, Failed, TimedOut, Stopped, and Skipped where present. Default to the last seven days and all states. Page 50 rows at a time with Previous and Next; no total count is required. The route and API must be available to Read users.

### Job-specific History

Within the existing manager-only job detail, show Created, Status, Trigger, Job revision, Started, Duration, and execution details action. Add the same period and status controls, defaulting to seven days/all states, and Previous/Next page controls. The fixed job ID is the filter; there is no need for a job picker. Preserve Manage access on the job detail and its existing history endpoint. Read users reach execution records via the Dashboard/full history route.

### Alerts

Keep separate Incidents and Deliveries tables and page them independently. Incident rows show Opened, Rule, Subject, State, Last observed, Resolved, and Safe code. Filter by period, state, and rule. Delivery rows show Incident context, Kind, Status, Attempts, Next attempt, Completed, and Safe code. Filter by period, status, and kind. The delivery's incident context should be readable without copying an opaque incident ID into the primary column; retain the ID where needed for correlation or linking.

Proposed operational default: Incidents initially show all Open/Unknown records regardless of opening date, with a switch to All/Resolved history that applies a recent period (30 days by default). A deliberately chosen date range applies to the selected set. This prevents an old unresolved incident from disappearing by default. The period filters by latest activity: LastObservedAtUtc for active incidents, and the later of LastObservedAtUtc and ResolvedAtUtc for resolved incidents. Within any selected set, order Open first, Unknown second, and Resolved third; within each state, newest activity first, then incident ID. Deliveries default to the last 30 days. Both tables use server-side pages of 50 and Previous/Next; their cursors and filters are independent. Rule and sink editors retain their current permissions and behavior.

### Audit

Keep the existing columns Time, Event type, Actor, Authentication mode, and Correlation ID. Add an exact event-type dropdown plus existing text search, date presets/custom range, and current Previous/Next navigation. Default to the last seven days. The dropdown should offer event types from the tenant's retained data, including types outside the current page, rather than only types visible in loaded rows. Allow a custom period spanning up to 365 days and targeting any retained date. Do not change the 365-day retention default. Use the existing Read permission and safe audit projection.

## Shared behavior and backend contract

- A shared filter-bar/page-control component may be used, but each API owns its valid filter values and query. Use query-string parameters for period, exact state/type, search, template/rule/job selectors, limit, and opaque cursor as relevant.
- Date ranges are inclusive at the start and exclusive at the end. Display times and interpret custom calendar dates in the user's selected timezone, then send UTC bounds. A preset uses the current time as its upper bound when applied. Show the active period and a clear empty state.
- Validate date ordering, supported statuses/types, text lengths, page size, and cursor shape server-side. A changed filter resets the cursor and Previous stack; a cursor cannot be reused with different filters or tenant context. Use timestamp descending then unique ID descending for stable keyset paging, with the incident state priority before its activity timestamp.
- Pages show live data. An incident whose state or latest activity changes while someone pages may move between pages; Refresh starts a new browse from the first page. A persistent cross-request snapshot and exact total count are outside this design.
- The full execution query joins the execution's stored job revision to display the template actually used at run time, not today's job configuration. Compute duration from persisted attempts consistently with job history; do not perform an N+1 details request from the browser.
- Keep read endpoints tenant-isolated and respect existing Read/Manage policies. List responses expose only existing safe fields and safe codes.
- Preserve load, empty, refresh-error, and stale states. Tables must remain usable on narrow screens with horizontal scrolling and accessible labels. Provide both English and pt-BR strings.
- Update API, SQL query, and UI tests for filtering, pagination, date boundaries, cursor invalidation, authorization, and safe projections. Exercise SQL-backed query behavior where existing integration fixtures support it.

## Builder's calls

- Choose component boundaries and route names consistent with the current tenant app.
- Choose exact SQL indexes after inspecting query plans and existing indexes; add migrations only if justified.
- Choose whether filter changes apply immediately or through an Apply button, while keeping page reset and displayed period clear.
- Choose compact formatting for elapsed durations, timestamps, and narrow-screen table overflow.
