# Task 7 report

Implemented scheduled and manual value contexts, per-run date overrides, and manual request identity checks.

## Changes

- Added `ManualRunOptions`, the overload on `IJobCommands`, and optional `referenceDateOverride` on the HTTP request.
- Added UTC-validating context factories. Scheduled context receives the selected occurrence time. Manual context receives the transaction clock time and optional override.
- Captured contexts for dynamic-capable templates at SQL and in-memory admission. SQL scheduled admissions use the occurrence selected by `OccurrenceWriter`; the SQL attempt store reloads the persisted context.
- Added an in-memory accepted-context seam and regression tests for late occurrence context, execution-scoped overrides, cross-midnight replay, changed-date conflicts, and unchanged job revision.
- Preserved the SQL legacy no-override request hash. Override hashes include the normalized ISO date. Manual scheduled-timestamp sources and legacy overrides are rejected.

## Verification

- Focused integration test command was attempted; restore failed with `NU1301` because NuGet.org credentials were unavailable.
- SQL integration test command was attempted; it stopped at `NETSDK1004` because the failed restore did not produce `project.assets.json`.
- A no-restore infrastructure build was also blocked by denied writes to the worktree `obj` directories. No test pass is claimed.

## Review follow-up

- Fixed `SqlJobCommands` to keep `TenantDatabaseOptions` distinct from `ManualRunOptions` when opening the business transaction.
- Bound `@context` before executing the scheduled execution insert.
- Wired the Scheduler composition root to a `SqlTemplateCatalog` built from its trusted SQL and HTTP descriptors, enabling capability detection for scheduled admissions.
- Changed the manual replay test clock advance to 23 hours 59 minutes so it crosses midnight inside the 24-hour deduplication window.
- Retried the focused test command and the SQL integration filter. Both remain blocked during restore by NuGet.org repository-signature TLS/authentication failure (`NU1301`, credentials unavailable). No tests are reported passing.

## Limitations

- SQL fixture behavior could not be exercised without successful NuGet restore and `WORKERDOCK_TEST_SQL`.
- Task 7 changes are scoped to the requested files; concurrent alert changes and the pre-existing Task 6 report edit were left untouched.
