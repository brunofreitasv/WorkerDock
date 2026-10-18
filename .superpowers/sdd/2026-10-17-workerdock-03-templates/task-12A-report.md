# Task12A report

Implemented protected tenant integration settings for the Task12A scope.

- Added audited, revisioned SQL vault and egress settings with tenant/deployment identity checks, rowversion ETags (stale writes return 412), and manager-only GET/PUT APIs.
- Added validated trusted-provider options and fixed tenant-jobs / tenant-alerts policies. Private destinations require an explicit bounded allowlist; deployment hard denials remain enforced by the worker.
- Added server-authored, nonsecret integration snapshots to child attempt descriptors. Incoming job DTOs cannot supply snapshots; secrets remain references resolved only at use time.
- Added Development in-memory settings adapter and EN/pt-BR Vault and Outbound settings screens.
- Added paired 0004b_integration_settings migrations, manifest checksums, rollback guard, SQL setup documentation, SQL and memory tests, and Playwright configuration/specs.
- Included the plan correction assigning Task12A migration ID 0004b_integration_settings after Task11 0004a_profiles.

Verification:
- Frontend production build: PASS.
- Frontend unit tests: 8/8 PASS.
- Playwright discovery: 2 tests found.
- SQL-independent integration tests: 245/245 PASS.
- Unit tests: 31/31 PASS (prior run; no unit source changed afterward).
- Release Web build: PASS, 0 warnings, 0 errors.
- Earlier full ProcessTests run: 32/33; only Disposing_active_noncooperative_child_confirms_tree_exit_without_lifecycle_race failed as wrapped ExecutorStartupFailed. Isolated rerun passed 1/1. The cause of the prior full-suite stress failure remains unestablished.
- SQL_TEST_BLOCKED: WORKERDOCK_TEST_SQL is not configured. No live SQL acceptance is claimed.
- PLAYWRIGHT_BLOCKED: npm.cmd run e2e -- --reporter=list failed before test execution because Chromium was absent: Executable doesn't exist at C:\Users\bruno\AppData\Local\ms-playwright\chromium_headless_shell-1248\chrome-headless-shell.exe. npm.cmd exec playwright install chromium attempted Chrome for Testing 156.0.8078.4 (https://cdn.playwright.dev/builds/cft/156.0.8078.4/win64/chrome-win64.zip, 197.5 MiB) but remained at 0% for about three minutes and was interrupted. No browser was found on PATH. Playwright test discovery passes; browser E2E did not run.

Migration hashes verified against database/tenant/manifest.json:
- up: 91469EB10137B347418F41488BE03A221BAD16131FD87DF113D2A07DD38B67D5
- down: B1A84221D9C15C19C9671B222AB5EFB6D213EBFE4339FEB3AD31378FC3C31859

