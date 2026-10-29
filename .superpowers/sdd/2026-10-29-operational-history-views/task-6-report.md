# Task 6 report: alert history

Implemented independent incident and delivery history tables with period/status/state/rule/kind filters, selected-timezone display, refresh, and cursor paging. Read endpoints return safe paged DTOs; incident ordering prioritizes Open, then Unknown, then Resolved, with latest activity inside each group. Open and Unknown remain in the default Active view regardless of age. Delivery rows include safe rule and subject context. Rule and sink editing remains Manage-only.

Added focused UI tests, SQL integration tests, and EN/pt-BR alert history help. Updated the alerts E2E viewer check to assert the incident rule cell using an exact role selector.

## Verification

- Focused alert UI: 3 files, 22 tests passed.
- Frontend build: center and tenant TypeScript checks and Vite builds passed; Vite emitted its existing chunk-size warning.
- Alerts E2E: 2 tests passed serially. The prior parallel run intermittently timed out on `page.goto`; a serial run with server reuse disabled started cleanly. A stale `RepeatedFailure` text expectation was corrected, then the rule assertion was made exact after strict-mode matched incident and delivery text.
- .NET integration project compiled with isolated output after the two unrelated malformed `\\n` lines in `SqlProfileSqlTests.cs` and `JobAdmissionTests.cs` were temporarily omitted and restored byte-for-byte (SHA256 verified). Alert SQL tests could not execute because `WORKERDOCK_TEST_SQL` is not configured (`SQL_TEST_BLOCKED`).

## Scope note

The worktree had unrelated pre-existing edits in `App.tsx` and `Program.cs`. Only the alert prop hunk in `App.tsx` and unavailable `IAlertQueries` adapter hunk in `Program.cs` are part of Task 6; other hunks must remain unstaged.

## Review fix round 1

- Active incident period is now explicit and visible. The period control remains displayed for every incident state; Active starts with its period checkbox off and sends no date bounds. Selecting a period enables the checkbox, switching state preserves the selected range, and unchecking it clears the Active bounds. Focused UI coverage verifies default-unbounded, enable, preset change, state switch/retention, and clear behavior.
- Added a SQL boundary test proving active rows use `LastObservedAtUtc`, resolved rows use the later of `LastObservedAtUtc` and `ResolvedAtUtc`, and `toUtc` is exclusive. The .NET test project compiled with isolated output; all three SQL tests are blocked at fixture construction because `WORKERDOCK_TEST_SQL` is unset. The two unrelated malformed test files were restored byte-for-byte and SHA256 verified.
- Verification: focused alert UI 23/23 passed; frontend center and tenant builds passed with the existing Vite chunk-size advisory; serial alerts E2E passed 2/2 after the review fix.
