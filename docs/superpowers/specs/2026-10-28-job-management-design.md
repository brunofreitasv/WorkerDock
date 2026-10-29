# Job management and monitoring recovery

Date: 2026-10-28
Status: Proposed; awaiting user approval before implementation.

## Intent and agreed requirements

After creating a job, managers must be able to find it again, edit its configuration, enable or disable it, and remove it from their working list. The user selected dedicated management list endpoints instead of reusing monitoring queries and requested deeper improvements to the job handling experience.

The user defined Delete as archival: disable the job, hide it from the current list, preserve its history, and make it available in an archived list. The user explicitly approved restoring an archived job to Disabled for editing and later enabling. They also reported `GET /api/monitoring/jobs?limit=50` returning 503 with `MonitoringUnavailable`.

The remaining choices in this document are implementation proposals, not decisions previously made by the user. The browser mockup uses synthetic data and makes no live changes.

## Findings in the current implementation

- `App.tsx` renders `ManualJobEditor` under Maintenance / Jobs. That wrapper opens a new `JobWizard`; there is no saved-job selector or job route.
- Monitoring's `JobsTable` displays rows without navigation or management actions.
- The wizard can update a job it already holds in memory, but cannot initialize from a persisted job ID after navigation or refresh. Its creation title and final activation stage are confusing for ordinary editing.
- Existing APIs support revision writes, enable/disable, archive, duplicate, manual runs, and schedule commands. There is no restore action.
- `SqlJobLifecycle` archives by disabling the job and tombstoning its schedule. `Enable` cannot restore an archived job. Archived configuration writes are not consistently rejected by all existing save paths.
- The wizard loads only the missed-work policy mode and replaces tolerance/catch-up values with defaults when saving. It also truncates saved one-time values for input display. Existing-job editing must preserve the complete saved representation unless the user changes it.
- The wizard saves configuration and schedule in separate requests. Partial success can leave a different combination from what the manager reviewed, especially for an enabled job.
- `SqlMonitoringQueries.JobsAsync` selects `COALESCE(s.Paused,0)` and reads it using `GetBoolean(8)`. The integer fallback makes this a likely SQL result type mismatch. The endpoint masks unexpected exceptions as the reported 503. Reproduce against SQL Server before declaring this the live incident's confirmed cause.
- The same jobs query filters the current revision timestamp using a default seven-day window, even though the UI offers no date filter. Old jobs can disappear independently of the 503.
- SQL integration tests use a disposable database through `WORKERDOCK_TEST_SQL`; that variable was absent during design exploration. Source review alone does not verify the production fix.

## Approaches

1. **Dedicated management queries and screen — selected by the user.** Add management-specific list/detail/history reads, reuse existing commands where their semantics fit, and give existing-job editing its own presentation. Management availability and job visibility do not depend on a monitoring date window or operational aggregations. This adds query contracts and UI work.
2. **Reuse monitoring for management — not selected.** Add actions to the monitoring table and show it in both navigation areas. This reduces initial UI code, but couples job administration to reporting filters, metrics, and failures.

Use the repository's React, TypeScript, MUI, ASP.NET Core, SQL Server, authentication, translation, validation, and ETag conventions. No new framework or UI dependency is needed.

## Proposed experience

### Find jobs

Maintenance / Jobs opens the inventory, not the wizard. The default view is **All current** (enabled and disabled, excluding archived), with **Enabled**, **Disabled**, and **Archived** views. This makes newly saved disabled drafts visible immediately and avoids confusing “active execution” with “enabled job.”

Provide debounced search by job name or template, stable alphabetical pagination, Refresh, and a prominent Create job button. Preserve the selected view, search, and page when returning from a job. State filters and job routes must survive reload and browser Back/Forward. Avoid storing job configurations in browser storage.

Rows show name, template/version, explicit status, schedule summary and timezone, configuration update time, and relevant actions. Configuration update time is the current revision's timestamp, not a claim that lifecycle changes updated that configuration. All saved jobs are eligible regardless of age or whether they have ever run. The first version does not require exact tab counts or arbitrary column sorting; the mockup's counts illustrate the layout only.

Make the job name a real link. Offer Edit and Enable/Disable directly; put less frequent actions such as Duplicate and Delete in a labeled overflow menu. Archived rows offer View and Restore. After archiving, confirm the result with a link to Archived. After restoration, show the restored job in Disabled with a link to open it.

Use distinct states for initial loading, no saved jobs, no search matches, permission loss, and a failed request. A failed refresh keeps the last successful rows with a stale-data message and Retry. Never display a failed request as an empty inventory. Disable pending mutations for their job without blocking unrelated rows, and avoid status changes that have not been confirmed by the server.

### Open, edit, and review

A stable route such as `#/jobs/<id>` opens job details. Use Overview, Configuration, Schedule, Execution policy, and History sections. Archived details remain readable. The job name, template version, and enabled/disabled/archived state remain visible above the sections.

Create job continues to use the guided creation flow. Saving a draft gives it a stable detail URL and makes it findable in the inventory even if the user leaves before enabling. A successful creation offers Open job and Back to jobs.

Edit opens an existing-job form with direct access to Configuration, Schedule, and Execution policy, reusing existing schema-driven controls. Do not force users through template selection and activation to rename a job or change one parameter. Keep an explicit unsaved indicator and a persistent Review and save changes action. The review shows changed sections and whether the job will remain enabled or disabled. Saving never implicitly enables or disables a job.

Load the pinned template and version, saved profile/catalog, complete execution and missed-work policies, dynamic bindings, timezone, and one-time offset. Default values apply only when genuinely creating new fields. Retired or unavailable dependencies remain identifiable; never substitute the first template/profile, clear values, or silently upgrade a template. If its descriptor is unavailable, show a readable summary and an actionable blocker; disable editing that cannot be represented safely while still allowing Disable, Archive, Restore, and History as appropriate.

Do not allow changing template identity/version in the first existing-job editor. Creating a different template job remains available through Create job. Preserve unknown supported configuration properties and exact unchanged temporal values when round-tripping.

Guard navigation away from unsaved edits, including in-app navigation, browser history, and reload where the browser permits. Offer Stay, Discard, and Save where applicable. Do not replace local edits during background refresh. On a stale ETag, keep the user's draft and require loading current server state and explicitly reapplying/reviewing changes before another write. No automatic overwrite or merge.

Show validation near the relevant fields and in a linked summary. If Enable fails, offer Open settings with a safe reason rather than a generic failure. Parse only the API's allowlisted problem code/field values; never show raw server exception details.

### Lifecycle and history

| Action | Result | Schedule and execution behavior |
| --- | --- | --- |
| Disable | Enabled to Disabled; remains in current inventory | Blocks new execution starts. Already running executions follow their existing lifecycle. |
| Enable | Disabled to Enabled after validation | Uses saved settings and existing admission rules. Accepted queued work may become eligible; enabling is explicit. |
| Delete / Archive | Current job to Archived and disabled | Removes it from current inventory, blocks new starts, preserves definition and history. Does not issue a stop command. |
| Restore | Archived to Disabled | Preserves identity/history and returns it for review. Does not run, enable, replay missed occurrences, or admit work. |
| Pause/resume schedule | Changes schedule state only | Shown only when a schedule exists; manual runs retain existing rules. Resume does not replay paused occurrences. |
| Duplicate | Existing current job to a new disabled copy | Retains the existing duplication contract; review the copy before activation. Do not imply that it copied execution history or a schedule. |

Use Delete as the discoverable row action, with a confirmation headed “Delete <name>?” and a clear “Move to Archived” button. Explain that configuration and history remain and that running work continues. Disable also explains the effect on running work. Restore needs no destructive-action confirmation; its success message states that the job is disabled. Enable requires a current, valid saved configuration. Unsaved edits must be resolved before lifecycle actions from the editor.

Run now remains explicit and uses the existing idempotency key behavior and optional reference date. It is available only for an enabled, non-archived job. Show the accepted execution link. History lists bounded, paginated executions for this job, including trigger, status, revision, start/creation time, and duration when known, linking to existing execution details. Archival does not delete revisions, executions, attempts, or audit events; existing retention rules continue to apply.

## Dedicated API design

All management endpoints require the existing Manage policy and tenant scoping. Existing Read-only monitoring permissions remain intact. Mutations retain antiforgery, actor audit, command receipts, and optimistic concurrency protections.

- `GET /api/jobs?view=current|enabled|disabled|archived&search=...&limit=50&cursor=...`: management summaries only, no configuration JSON and no monitoring aggregates. Default current; bounded page size 1–100 and search length 100. Stable name/JobId keyset pagination, parameterized literal search, validated opaque cursor tied to the view/search. Do not filter by configuration creation date. Return items and nextCursor.
- `GET /api/jobs/{id}`: coherent management detail snapshot with revision/configuration/policy, lifecycle state, full current schedule and missed-work policy, and the job ETag. Read configuration, status, and schedule consistently so the editor does not combine different versions. Keep existing configuration and schedule endpoints compatible.
- `GET /api/jobs/{id}/executions?limit=50&cursor=...`: bounded history for the exact job, available when archived. Order by execution creation time/ID descending. No implicit seven-day limit; retention determines available history. Use the existing execution-detail endpoint for attempts and logs. An empty history is a successful empty page; an unknown job is 404.
- `PUT /api/jobs/{id}/definition`: a complete existing-job edit containing the revision fields and explicit schedule selection (none or full definition/policy). Require If-Match. Validate and save configuration, policy, and schedule within one business transaction, with the existing audit and command receipt conventions. Preserve the lifecycle state and schedule pause state; never reset a schedule cursor for an unrelated name/configuration edit. If validation, concurrency, or any write fails, no part of the definition is committed. Do not nest independently committing command calls.
- Extend existing `POST /api/jobs/{id}/actions` with `Restore`. Reuse Enable, Disable, Archive, and Duplicate. Restore only changes an archived job to disabled and writes an audit event and receipt. Invalid transitions return an explicit safe conflict; repeated requests must not accidentally enable a job.

For an enabled job, validate the entire proposed definition with the same activation requirements before publishing it. Updates affect subsequently accepted executions; executions already accepted remain pinned to their original job revision. Read and write paths must use consistent locking/version checks with schedule and lifecycle commands. Reject configuration or schedule modification while archived, including through legacy endpoints, so API callers cannot bypass the UI's restore requirement.

SQL is the production implementation. Follow the existing adapter registration and fail-closed behavior when persistence is unavailable. Do not return an empty successful list from an unavailable adapter. Builders may add a memory implementation if the existing development adapter can support it faithfully; SQL integration coverage is still required.

## Reversible archive and schedules

Proposal: preserve an existing schedule definition when archiving instead of treating archive as schedule deletion. Set the job disabled/archived, invalidate its schedule revision, and advance/reset recovery boundaries as appropriate. Do not change an already removed schedule into an active one. The scheduler, occurrence materializer, queue claim, and retry gates already inspect job state; verify each against the changed archive contract. No new starts may occur while archived or restored-but-disabled.

Restore clears Archived while retaining Enabled=false and the stored pause choice. The subsequent explicit Enable resets the schedule activation boundary under existing rules so time spent disabled/archived is not automatically replayed. It may release existing accepted work under the existing admission rules, which must be distinguished from materializing missed schedule occurrences.

For jobs archived by the old implementation, `Removed=1` cannot reliably distinguish an intentionally deleted schedule from one removed by archival. Never silently resurrect such a schedule. The detail API may expose the retained tombstone as previous schedule settings for explicit review/reapplication. Show that no current schedule is configured and require explicit review of manual-only versus reapplying that schedule before enabling from the restored workflow. New archives preserve the current schedule; no heuristic migration is needed.

This approach should not require new schema solely for archive/restore. If implementation reveals a required durable marker or index, use the repository's migration/snapshot conventions and document why rather than editing a live database ad hoc.

## Monitoring repair

Reproduce the reported request with a nonempty SQL-backed jobs list, including manual, scheduled, paused, and removed-schedule jobs. Confirm the database result type at the pause column and the thrown exception before fixing it. Ensure the projection returns an actual SQL bit for the Boolean reader, with false for no active schedule. Add SQL integration coverage so an empty list cannot conceal a row-conversion bug.

Remove the invisible default revision-age cutoff from the jobs inventory used by Monitoring. Explicitly supplied date filters may retain their documented validation/meaning; do not change Audit's date rules or the operational 24-hour counters. Log a safe server diagnostic with correlation information when an unexpected monitoring error occurs while preserving the safe API response.

Keep loading, initial failure, stale refresh, and true empty states distinct. Managers can open a job from Monitoring through a Manage link to the new details route; viewers keep read-only access.

## Constraints and scope boundaries

- Match existing MUI styling, navigation, English/pt-BR labels, timezone preferences, and keyboard/focus conventions. Use text status labels, accessible menus/dialogs, responsive form layouts, and a horizontally scrollable table or stacked layout on narrow screens.
- Preserve immutable revision history and accepted execution snapshots; no hard delete or automatic running-execution cancellation.
- No bulk actions, new scheduling engine, template version migration UI, browser-stored secret/configuration drafts, or changes to retention policy in this work.
- The mockup demonstrates the proposed navigation, list, editing, history, archive, and restore experience. Its simplified fields, synthetic metrics, sample counts, and simulated creation shortcut are not new API promises.

## Acceptance evidence

1. Create and save a disabled draft; navigate away/reload; find it; enable it; reopen and edit it without creating another job.
2. All current/Enabled/Disabled/Archived views, search, pagination, stable routes, and return-to-list state work for jobs older than 90 days and jobs that never executed.
3. Existing definitions round-trip without changing dynamic bindings, profile/catalog values, nondefault policy fields, schedule pause state, timezone, one-time offset, or unchanged timestamp precision.
4. Atomic edit failure leaves both revision and schedule unchanged. Enabled saves retain enabled status and validate before commit. Stale ETags preserve local edits and reject writes without partial changes.
5. Disable/enable and archive/restore work after a page reload. Archive blocks new starts and retains running execution/history; restore remains disabled and does not materialize missed work. New archive restores preserve schedules. Legacy removed schedules require explicit reapplication.
6. Archived legacy API writes are rejected. Viewer requests to all new management reads and mutations are denied. SQL errors yield safe failures, not success with missing data.
7. SQL-backed monitoring returns 200 with correct pause booleans and older job rows. Failure/retry UI states retain prior successful data where applicable.
8. Browser coverage exercises create → reopen → edit → disable → enable → archive → locate in Archived → restore → edit, plus unsaved-navigation and concurrent-edit recovery. Check English/pt-BR, keyboard use, and narrow layout.

Use focused SQL integration tests, endpoint authorization tests, UI behavior tests, and frontend/backend builds. Provision or use the approved disposable SQL test environment when implementing; if unavailable, report that verification as blocked rather than claiming a SQL fix is verified.

## Left to the builder

Choose component/service names, exact DTO shapes within these semantics, shared transaction helper boundaries, bounded query implementation, and styling details that match the existing code. Reuse the existing domain validators and input conversion logic. Keep the user-visible flows, lifecycle boundaries, dedicated query separation, and acceptance cases above intact. The next artifact after approval is an implementation plan.
