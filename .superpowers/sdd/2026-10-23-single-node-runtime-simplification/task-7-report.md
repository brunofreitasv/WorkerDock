# Task 7 implementer report

Status: DONE_WITH_CONCERNS — Task 7 runtime/API/settings changes are implemented and focused UI/build checks pass. SQL execution is blocked without `WORKERDOCK_TEST_SQL`; legacy distributed schema remains for the Task 8 retention and Task 9 Quartz/fresh-baseline work.

## Implementation

- Manual request idempotency now lives on `Executions`, with a filtered unique index and 24-hour expiration. Command receipt state is written to the execution row. Existing request scope and same-execution retry semantics are retained.
- `HoldsJobSlot` on `Executions` replaces the active `JobPermits` read/write paths in admission, attempt claiming and projection validation. Job limits remain on `Jobs`; template limits move to `TemplateCatalog.ConcurrencyLimit` and return an `Applied` receipt.
- Egress policy JSON is consolidated into `IntegrationSettings`; migration 0012 backfills it and removes `TenantEgressPolicies`.
- Monitoring and `/api/capacity` report one `RuntimeStatus` row. Runtime readiness requires the status row to be ready with a heartbeat no older than 30 seconds; dashboard and health use the same freshness window. Monitoring UI shows runtime readiness/host capacity, without worker editing or projection-lag state.
- Kept `TemplatePolicies`, `PolicyRevisions`, `Workers`, `WorkerCapabilities`, `JobPermits`, and `ManualRequestDedup` in the transitional schema because `SqlCapacityStore`/Quartz compatibility code and current-state retention procedures still reference them. The moved manual/admission paths no longer use those tables. Task 8 owns retention-procedure reconciliation; Task 9 owns Quartz removal and the fresh-install baseline.
- Added an isolated owner-row limit SQL test and manual retry SQL test. The catalog SQL test assertion was edited in the worktree but left unstaged because it shares a hunk with the pre-existing 0010 custom-template test rewrite.

## Verification

- `dotnet build WorkerDock.slnx --no-restore`: passed, 0 warnings and 0 errors.
- `dotnet test ... --filter 'FullyQualifiedName~Template_and_job_limits_are_applied_on_their_owner_rows'`: compiled; SQL blocked at fixture startup (`SQL_TEST_BLOCKED`, `WORKERDOCK_TEST_SQL` unset).
- Focused SQL filters: 5 passed; 3 capacity cases blocked at the same SQL guard. Manual retry/24-hour SQL behavior is unverified without SQL Server.
- `npm run build --workspace @workerdock/tenant`: passed (TypeScript and Vite build).
- Focused `WorkersPage`/`Dashboard` tests: passed, 2/2. Isolated JobWizard, SessionProvider, AlertRulesPage and ControlCenter files passed 15/15, 16/16, 17/17 and 2/2 respectively.
- Full frontend test run: 145 passed, 12 failed. Failures were in SessionProvider (1), AlertRulesPage (5) and JobWizard (6); these test files passed individually. The one JobWizard label lookup failure (`Use a schedule`) occurred only in the broad run. The remaining broad-run failures were timeouts under suite load.
- Migration 0012 checksums: up `2BEDD727AD5460EABD4789F59090F000F177257F1FCA8AA455CDDEE3C21E8016`; down `ADA5B5B6649C99868B9FBCC71245F43949BCFE5E6D43ACED2E2720D85C166DC4`.

## Staging

Pre-existing 0010 migration files, its manifest entry, and the 0010 preflight/test changes remain unstaged. Mixed `Program.cs`, `SqlJobCommands.cs`, `SqlTemplateCatalog.cs`, `CommandOutcomeTests.cs`, `IntegrationSettingsTests.cs`, `CapacityPolicySqlTests.cs`, and `TemplateCatalogSqlTests.cs` were staged by Task 7 hunks only where the hunks were separable. No docs, settings UI, Quartz fixtures, or 0010 work were staged.
