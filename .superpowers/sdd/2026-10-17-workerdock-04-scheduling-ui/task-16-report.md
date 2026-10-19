# Task 16 report — Operational dashboards and execution history

Implemented safe SQL monitoring projections and Read-authorized dashboard, job paging, execution detail/timeline, worker/capability, and audit endpoints. Filters and pages are bounded and parameterized; cursors use unique-ID tie breaks. DTOs omit job configuration, SQL/body content, profile and secret values, and audit detail payloads. Readiness is componentized and health-only credentials resolve through `ISecretVault`; they do not create a user session or grant application access. The default web registration remains the development vault adapter; production must register its managed vault provider.

Added English/pt-BR operational views, visible-only five-second polling, hidden-page pause, failure backoff, stale-data timestamps, safe detail links, and monitoring E2E scenarios. Documented endpoints, bounds, polling, health credential setup, and SQL limitations in `docs/monitoring.md`. The monitoring query contract includes the planned `WorkersAsync` projection because worker/capability UI otherwise had no data source.

Verification:

- `dotnet test tests/WorkerDock.IntegrationTests --filter 'FullyQualifiedName~MonitoringAccessTests|FullyQualifiedName~AccessPolicyTests'`: PASS, 32/32.
- `npm.cmd --prefix frontend test -- --run`: PASS, 30/30 across 5 files, including an unmount/deferred-request regression for polling cleanup.
- Focused `useVisiblePolling.test.tsx`: PASS, 3/3. The deferred request test was RED before adding the active-effect scheduling guard.
- `npm.cmd --prefix frontend run build`: PASS (TypeScript and Vite production build).
- `npm.cmd --prefix frontend run e2e -- --list`: PASS, 16 tests in 5 files.
- `npm.cmd --prefix frontend run e2e -- monitoring.spec.ts`: `PLAYWRIGHT_BLOCKED`; browser executable is missing at `C:\Users\bruno\AppData\Local\ms-playwright\chromium_headless_shell-1248\chrome-headless-shell-win64\chrome-headless-shell.exe`.
- `git diff --check`: PASS.
- SQL-backed projection/readiness/sentinel verification: `SQL_TEST_BLOCKED`; `WORKERDOCK_TEST_SQL` is unset. Database failures are surfaced as unavailable responses rather than empty/healthy state.

The E2E scenarios are present and discovered but could not execute without Chromium. Production secret-provider integration remains a host configuration dependency for Task 20.

Task 16 Important review follow-up: polling now checks that its effect is still active before scheduling after a request resolves. The regression unmounts during an in-flight request, resolves it, advances fake time by 60 seconds, and confirms the loader is not called again. Full frontend tests pass 30/30 and the production build passes.
