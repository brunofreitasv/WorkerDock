# Task 15 report

Status: DONE_WITH_CONCERNS

Implemented the six-step job wizard and connected it to existing template, configuration, schedule, preview, lifecycle, run and stop endpoints. Schedule edits preserve local date/time, explicit offset, and zone as separate input values; the UTC preview comes from the server. Drafts stay disabled until the operator reviews and enables them with the current ETag. Stale edits preserve their input and refresh the current version. A failed schedule save preserves the created job ID. Duplicate review loads the disabled copy for editing before activation. Added schedule deletion confirmation, pause/resume, disable/enable/archive, idempotent run retry, stop confirmation, safe pending/paused details and an explicit no-timeout option. Added a Read-authorized `/api/time-zones` response with Windows identifiers converted to IANA identifiers where available. English and Brazilian Portuguese labels, help and errors are included.

Browser scenarios were added before production UI. Playwright discovery passes with 4 tests in 2 files. The browser scenarios could not execute, so no application RED was observed: the invocation fails before test setup because the configured Chromium executable is absent at `C:\Users\bruno\AppData\Local\ms-playwright\chromium_headless_shell-1248\chrome-headless-shell-win64\chrome-headless-shell.exe`. Status: `PLAYWRIGHT_BLOCKED`.

Verification:

- `npm.cmd --prefix frontend run build` — PASS.
- `npm.cmd --prefix frontend run test -- --run` — PASS, 13/13 tests.
- `npm.cmd --prefix frontend run e2e -- --list scheduling.spec.ts job-management.spec.ts` — PASS, 4 tests discovered.
- `npm.cmd --prefix frontend run e2e -- scheduling.spec.ts job-management.spec.ts` — `PLAYWRIGHT_BLOCKED`; missing Chromium executable as listed above.
- `dotnet build src/WorkerDock.Web/WorkerDock.Web.csproj --no-restore` — PASS, 0 warnings and 0 errors.
- SQL-backed lifecycle and live migration checks remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset (Task 14 limitation).
- `git diff --check` — PASS.

Ruling: Preserve the draft's local time and offset as raw form values and use the API preview as the only UTC authority. This prevents an en↔pt-BR display change from reinterpreting an ambiguous local instant; the cost if this assumption is wrong is that locale-specific time editing would require a distinct conversion workflow.

Concern: Browser behavior, keyboard flows, and responsive layout remain unverified until the configured Chromium build is installed. The four scenarios are present and discoverable.

Commit: `b1d99da` (`feat: add guided scheduling and job management workflows`).

## Important review follow-up

The stale ETag flow now keeps the current ETag unchanged and blocks Save and Enable after a 412. The operator must explicitly load and see the current server revision before the ETag is refreshed. The conflicted draft remains recoverable through “Restore my draft”; restored values require a separate review acknowledgement before saving or enabling. Regression scenarios assert there is no retry with the stale ETag and that the rebase uses the newly loaded ETag.

Saving a previously scheduled job with scheduling switched off now sends DELETE with If-Match and then GETs the schedule. The UI reports manual-only state and permits enable only after GET confirms 404; failed or pending deletion preserves schedule state and blocks enable. The in-wizard language selector uses the existing preference state without unmounting the wizard. Its unit regression verifies draft name, local datetime, offset and preview UTC across en↔pt-BR.

Follow-up verification: `npm.cmd --prefix frontend run build` PASS; `npm.cmd --prefix frontend run test -- --run` PASS, 14/14 tests; focused `JobWizard.test.tsx` PASS; Playwright discovery PASS, 4 tests/2 files; `git diff --check` PASS. Browser execution remains `PLAYWRIGHT_BLOCKED` with the same missing Chromium executable. Follow-up commit: `88d1e93` (`fix: safely resolve scheduling UI review findings`).

## Important review follow-up 6

Once-schedule confirmation now compares parsed offset durations, so the restored local `-05:00` value matches the server's `-05:00:00` TimeSpan representation. Malformed non-null offsets cannot confirm each other. Added direct semantic comparison coverage for negative, positive, and zero offsets, plus an end-to-end component regression for restoring the Once draft, saving against the seconds-form server value, and enabling after matching PUT+GET.

Verification: focused `JobWizard.test.tsx` PASS, 12/12 tests; frontend unit suite PASS, 25/25 tests; build PASS; Playwright discovery PASS, 7 tests/2 files; `git diff --check` PASS. Browser execution remains `PLAYWRIGHT_BLOCKED` because the configured Chromium executable is unavailable. SQL-backed lifecycle/live migration checks remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset. Commit: `fix: compare semantic schedule offsets`.

## Important review follow-up 7

After a successful schedule PUT, Once-schedule confirmation now accepts a null request offset when the GET returns a parseable concrete resolved offset for the exact same kind, zone, and local time. This equivalence is limited to post-write confirmation; conflict revision review remains strict. Null-to-null and malformed returned offsets stay unconfirmed, and the backend's ambiguous-local-time rejection is unchanged. Added a New York winter Once round-trip regression and direct rejection checks for null, malformed, and mismatched local times.

Verification: focused `JobWizard.test.tsx` PASS, 14/14 tests; frontend unit suite PASS, 27/27 tests; build PASS; Playwright discovery PASS, 7 tests/2 files; `git diff --check` PASS. Browser execution remains `PLAYWRIGHT_BLOCKED` because the configured Chromium executable is unavailable. SQL-backed lifecycle/live migration checks remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset. Commit: `fix: confirm resolved once schedule offsets`.

## Important review follow-up 2

Schedule removal now tracks persisted state as `none`, `present`, or `unknown`. DELETE failure and successful DELETE with an unconfirmed GET both remain visibly unresolved; the review does not claim “Manual job,” manual Enable stays disabled until a schedule GET confirms 404, and Pause/Disable remain available as safety actions. Duplicate review resets state from the target schedule GET, allowing a scheduled source to become a manual duplicate only when the target returns 404. Added unit regressions for unconfirmed schedule removal and scheduled-source duplication, plus Playwright scenarios for DELETE failure, GET failure, and duplicate state.

Verification: `npm.cmd --prefix frontend test -- --run` PASS, 16/16 tests; `npm.cmd --prefix frontend run build` PASS; Playwright discovery PASS, 7 tests/2 files; `git diff --check` PASS. The browser attempt stops before setup with `PLAYWRIGHT_BLOCKED`: the configured Chromium executable is missing at `C:\Users\bruno\AppData\Local\ms-playwright\chromium_headless_shell-1248\chrome-headless-shell-win64\chrome-headless-shell.exe`. SQL-backed lifecycle/live migration checks remain `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset. Commit: `c3d026e` (`fix: preserve uncertain schedule state in job UI`).

## Important review follow-up 3

Enable now accepts either confirmed manual state (`none`) or a confirmed persisted schedule that is selected for use (`present` and `scheduleEnabled`). A still-present schedule while the draft is toggled off and an unresolved schedule state remain blocked. Safety Pause/Disable controls are limited to uncertain or removal-pending schedule states. Added unit regressions for scheduled-draft activation and for confirming schedule absence before manual activation; unresolved-removal and duplicate regressions remain in place.

Verification: focused `JobWizard.test.tsx` PASS, 5/5 tests; frontend unit suite PASS, 18/18; build PASS; Playwright discovery PASS, 7 tests/2 files; `git diff --check` PASS. Browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium executable). Commit: `596f8c5` (`fix: allow enabling confirmed scheduled jobs`).

## Important review follow-up 4

Schedule writes now remain `unknown` until the schedule PUT succeeds and a schedule GET confirms the persisted schedule. PUT failures preserve that unresolved state and block activation. A 412 during the initial save now uses the newly created job ID to require explicit current-revision loading; the local schedule draft remains recoverable and needs another review before retry. The primary Playwright flow follows that re-review sequence.

Verification: focused `JobWizard.test.tsx` PASS, 7/7 tests; frontend unit suite PASS, 20/20; build PASS; Playwright discovery PASS, 7 tests/2 files; `git diff --check` PASS. Browser execution remains `PLAYWRIGHT_BLOCKED` (missing Chromium executable). Commit: `c492d93` (`fix: confirm schedule saves before activation`).

## Important review follow-up 5

Restoring a local schedule after loading a conflicting server revision now marks it dirty and unconfirmed. Enable remains blocked until schedule PUT succeeds and GET returns an input matching the submitted schedule. Unresolved writes and removal state retain their separate safety behavior. Added a regression where server cron `0 8 * * 1-5` differs from the restored draft cron `0 9 * * 1-5`; activation stays blocked until PUT and GET confirm the restored cron.

Verification: focused `JobWizard.test.tsx` PASS, 8/8 tests; frontend unit suite PASS, 21/21; build PASS; Playwright discovery PASS, 7 tests/2 files; `git diff --check` PASS. Browser remains `PLAYWRIGHT_BLOCKED` (missing Chromium executable). Commit: `ed2ad13` (`fix: mark restored schedules unconfirmed`).
