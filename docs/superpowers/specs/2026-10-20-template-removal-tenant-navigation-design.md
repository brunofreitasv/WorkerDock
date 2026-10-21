# Template 1.0.0 removal and tenant navigation

Date: 2026-10-20
Status: Chat design approved; written spec awaiting user review.
Workspace: C:\Users\bruno\.codex\worktrees\template-max-properties\WorkerDock 2

## Purpose and agreed outcomes

WorkerDock should offer only version 1.1.0 of its built-in HTTP and SQL Server templates. Existing saved SQL-backed HTTP and SQL Server job revisions pinned to 1.0.0 must be rewritten automatically to 1.1.0; 1.0.0 execution support must be removed. The tenant UI should group pages by task, keep access mode and personal display preferences only in Settings, and show the configured tenant display name beside the logo and in the browser tab. The user reported 503 responses while running the Development Memory adapter and requested an explanation with no code change for those responses.

## Current state

The Web, Scheduler, Worker, and Executor each register both template versions. The SQL profile diagnostic creates a 1.0.0 SQL job. The catalog API lists stored versions, including retired entries. Tenant JobRevisions are immutable and refer to catalog rows; WorkerCapabilities also refer to catalog rows. Existing migration scripts and their checksums are part of deployed schema history.

The UI has one flat navigation list. AppShell displays an access panel on every page. App renders personal language and time zone preferences at the bottom of every page. The Settings route is currently limited to Manage users. The header and HTML title are hardcoded to WorkerDock, while the editable tenant display name is loaded only inside the management Settings form.

In Development with Tenant:Adapter=Memory, monitoring, scheduling, and alert services intentionally use unavailable implementations. Their 503 responses, including dashboard polling failures, are expected in that mode. This design makes no change to those endpoints, pages, or polling behavior.

## Template migration

Add a new tenant migration; do not modify prior migration files. The upgrade follows the existing stopped-service, verified-backup process. Before writing, preflight must establish that:

- Both built-in 1.1.0 catalog entries exist with their trusted metadata.
- No execution references an HTTP or SQL Server 1.0.0 job revision. The user reports no past executions; the preflight enforces that assumption for each database.
- Every stored 1.0.0 job configuration validates against the corresponding 1.1.0 schema and semantic authoring rules.
- There are no other database references that would prevent removal of 1.0.0 catalog rows.

If any check fails, report a specific upgrade blocker and leave the database unchanged. The operator can correct the data or restore the matching release before retrying.

In one transaction, temporarily replace or disable the immutability guards solely for the migration, update every matching JobRevisions.TemplateVersion from 1.0.0 to 1.1.0, remove stale 1.0.0 worker capability rows, remove the two 1.0.0 catalog rows, and restore the guards. Verify after the transaction that no built-in 1.0.0 job revision, capability, or catalog row remains. The migration must be idempotent under the repository's migration runner and must not rewrite unrelated templates or versions. The down migration refuses reversal because the original version assignments cannot be reconstructed after the rewrite; rollback uses the verified database and release backup under the existing upgrade process.

The Web, Scheduler, Worker, and Executor register only 1.1.0 descriptors and implementations after migration. Built-in template defaults become 1.1.0; legacy-specific branches and 1.0.0 runtime support are removed. SQL profile diagnostics create and require 1.1.0 SQL jobs. Catalog list and picker responses contain only supported versions. Historical migration scripts may still contain 1.0.0 literals because changing them would invalidate recorded checksums.

Memory-mode data is disposable and does not survive a process restart. A new Memory instance registers only 1.1.0, so no persistent Memory migration is needed.

## Navigation and Settings

The sidebar has three labelled groups in both supported languages:

- Monitoring: Dashboard, Job monitoring, Workers, Alerts.
- Maintenance: Jobs, Templates.
- Administration: Documentation, Audit, Settings.

Preserve route identifiers and direct links. Keep permission filtering: readers see available read pages, managers also see management pages. Settings becomes visible to all readers. Its Personal view shows the current access mode, read or manage status, the anonymous-mode warning when applicable, and personal language and time zone controls. Tenant configuration, secret provider, outbound policy, SQL profiles, and retention remain Manage-only views. Remove the shared access panel from AppShell and the repeated preferences card from App. Group headings and links remain keyboard accessible and work in narrow layouts.

## Tenant display name

Allow Read access to the GET settings representation, while PUT settings remains Manage-only. The read representation contains only the existing display name, display time zone, and version, with no secret material. Load it into one app-wide settings state. Pass the display name to AppShell for the text beside the logo and set the browser document title to that same name. A successful Settings save updates both immediately; a page reload fetches the persisted value. Use WorkerDock as the temporary or error fallback when settings cannot be loaded. Preserve the current stale-version check, validation, and error display when saving.

## Error handling and verification

Migration checks fail before mutation and return a clear blocker for an execution, missing catalog entry, incompatible configuration, or unexpected reference. The migration transaction restores database immutability protections on success and rolls back on error. No browser display-name update occurs after a failed save.

Verify the SQL migration against a database with representative HTTP and SQL 1.0.0 revisions, including multiple historical revisions and archived jobs; verify it aborts unchanged when an old execution exists. Test that registration, catalog API, job creation, worker capabilities, and SQL profile diagnostics use only 1.1.0. Verify grouped navigation, stable deep links, reader access to Personal settings, management gating, preference persistence, and header/browser title changes after save and reload. Update English and Portuguese help and operational upgrade guidance to describe the single supported built-in version and the migration preflight. Run the relevant frontend, .NET, and database integration checks available in the development environment.

## Scope boundary

The 503 responses in Memory mode are explained by its unavailable service registrations. The user explicitly chose no code change for them.
