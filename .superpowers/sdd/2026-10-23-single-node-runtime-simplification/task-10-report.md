# Task 10 report: Documentation and end-to-end verification

## Changes

- Rewrote the README, architecture, scheduling, database, migration, execution, monitoring, alert, configuration, deployment, setup, production deployment, tenant management, job management, retention, SQL tutorial, and release checklist guidance for one Windows runtime service per tenant with bounded execution children.
- Documented SQL Server 2022+, fresh-install-only `0001_initial`, no upgrade converter, runtime identity restrictions, polling, process limits, and operational recovery. Preserved SQL secret and outbound-policy guidance and clarified the optional Control Center and Development memory mode.
- Removed `docs/reference/quartz-integration.md`, Quartz package attribution, obsolete example installation ID, and stale Quartz configuration references in supporting docs.
- Updated English and pt-BR operator help for first job, workers, troubleshooting, retention, and templates. The pre-existing getting-started, job-inputs, settings, and templates edits were reconciled; only Task 10 hunks are included in the Task 10 commit.
- Added `frontend/e2e/single-runtime.spec.ts` to inspect Dashboard, Monitoring, Workers, Alerts, Audit, Jobs, Retention, and Help views for visible Quartz/projection language. Corrected the API fixture order and response shapes after Playwright exposed the masked session and alert data.
- Removed the obsolete projection-pending receipt/status and warning from the job actions, details, and wizard UI, and removed their locale labels. Updated only directly affected E2E fixture fields in addition to the new spec.

## Verification

- `npm.cmd --prefix frontend run build`: passed (Center and Tenant builds; Vite reported the existing large-chunk advisory).
- `npm.cmd --prefix frontend test -- --run`: 28 test files and 155 tests passed; `JobWizard.test.tsx` and `AlertRulesPage.test.tsx` each had a 5-second timeout. Those two files passed isolated with `--testTimeout=15000` (2 files, 32 tests). A full rerun with a 15-second timeout also had 155/157 pass; `ControlCenter.test.tsx` and one alert-rule test exceeded 15 seconds while the process project ran concurrently.
- Focused Playwright single-runtime check: passed (1/1), after fixing route fixtures.
- Full `dotnet test WorkerDock.slnx`: exit 1 after building all projects. Unit tests passed (133/133). Integration tests reported 299/426 passed; SQL-backed cases failed with `SQL_TEST_BLOCKED` because `WORKERDOCK_TEST_SQL` is unset. One deployment-documentation assertion failed against an earlier wording change; the corrected wording passed its focused test (1/1). Process tests passed 56/57; `ExecutionControlTests.Finite_timeout_allows_thirty_second_grace_then_contains_the_process_tree` timed out waiting for its child PID signal. Its isolated run passed (1/1).
- Full process-project rerun during the concurrent frontend test run passed 53/57. The four failures were `ExecutionActivityTests.Completed_execution_emits_worker_dock_lifecycle_activity` (final state assertion at line 40), `DynamicExecutionTests.New_runtime_executes_legacy_fixed_job_without_context` (`ExecutorStartupFailed`), `ContainmentTests.Malformed_diagnostic_frame_is_dropped_before_terminal_result` (`ExecutorStartupFailed`), and the finite-timeout test above (`WaitForPids` timed out after 30 seconds). All four passed in isolated runs (four runs, 1/1 each), so none reproduced without the concurrent suite load.
- Obsolete-reference scan excluding the new test spec: no matches in README, source, database, docs, or frontend app/help. The exact required broad scan reports the intentional test-only regex in `frontend/e2e/single-runtime.spec.ts` (`Quartz|projection`), which provides the requested assertion.
- Required broad obsolete-reference scan: its only hit was the intended regex at `frontend/e2e/single-runtime.spec.ts:41`. A second scan of active docs/app paths excluding that test returned no matches. `git diff --check` and `git diff --cached --check` passed; only Git line-ending normalization warnings were reported.
- `WORKERDOCK_TEST_SQL`: unset. Live SQL integration/process verification is UNVERIFIED.

## Scope and review

The Task 10 commit contains only documentation/help updates, the new visible-view E2E spec, the narrow projection-pending UI removal and its directly affected mocks/tests, the example config cleanup, and this report. Pre-existing edits to app shell, settings, templates, API implementation, integration tests, and load tooling remain unstaged. No unrelated app changes are included.
