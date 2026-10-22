# Manual Deployment and Database State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let developers deploy WorkerDock with their own tools and explicitly prepare tenant SQL through either a manual PowerShell migration command or a desired-state SQL snapshot for a fresh database.

**Architecture:** Move the existing transactional migration engine into `WorkerDock.Infrastructure` and expose it through an optional PowerShell script that accepts a caller-owned `PSCredential`. Keep versioned SQL files as the incremental source and add a checked-in consolidated snapshot for empty databases. Remove the .NET migrator executable and repository deployment/package lifecycle scripts; rewrite current operator guidance around application requirements and manual checkpoints.

**Tech Stack:** .NET 10/C#, PowerShell on a .NET 10-compatible host, Microsoft.Data.SqlClient, SQL Server 2022+ at compatibility level 160, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-22-manual-deployment-and-database-state-design.md`

## Global Constraints

- Preserve all pre-existing dirty worktree changes, especially the pending `0010_remove_builtin_v1` SQL/manifest/template work; stage only each task's hunks.
- `database/tenant/manifest.json` and historical `.up.sql`/`.down.sql` checksums remain the incremental source; do not rewrite historical scripts.
- Application startup never applies migrations. Tenant runtime SQL retains its `ISecretVault` password reference and required TLS certificate validation.
- The manual migration script accepts a caller-supplied `PSCredential`; it creates no backup and manages no application processes or release files.
- The plain SQL snapshot is valid only for a newly provisioned empty database and is the only database action on that path; its rendered input includes a tenant GUID and required initialization state.
- Remove repository install, upgrade, uninstall, and package-builder scripts, custom ZIP manifests, and the DatabaseMigrator executable/project. Do not add a replacement deployment pipeline.
- A real disposable SQL Server is required to claim snapshot parity or live migration success. Missing `WORKERDOCK_TEST_SQL` is `SQL_TEST_BLOCKED`, not a passing test.
- The current Worker project targets `net10.0-windows`; describe actual runtime support without mandating IIS, a Windows service manager, or one deployment product.

## Review Focus

1. A missing or malformed `PSCredential`, SQL host, port, database, or assembly path must fail before SQL access and never echo the password (Task 2 tests).
2. A history hole, unknown history row, bad checksum, wrong tenant GUID, or target behind the installed state must fail before a new migration writes (Task 1 tests).
3. An untrusted SQL certificate must never be bypassed by the operator script's connection builder (Task 2 builder tests).
4. An unrendered tenant GUID or nonempty database must make the snapshot fail before creating WorkerDock objects (Task 3 tests).
5. Drift in snapshot triggers, grants, required seed rows, or history checksums must fail the migration-versus-snapshot parity test (Task 3 tests).

## File map and task dependencies

| Unit | Files and responsibility |
| --- | --- |
| Migration engine | Move `src/WorkerDock.DatabaseMigrator/{MigrationRunner.cs,SqlServerPrerequisites.cs,LegacyTemplateRemovalPreflight.cs}` to `src/WorkerDock.Infrastructure/Migrations/`; remove `MigrationRunner.Upgrade.cs` and `Program.cs`. `MigrationRunner` remains the testable transactional API. |
| Operator connection and script | Add `src/WorkerDock.Infrastructure/Persistence/TenantSqlConnectionBuilder.cs` and `scripts/Invoke-TenantMigration.ps1`; update `TenantSqlConfiguration.cs` and `TenantSqlConnectionResolver.cs` so runtime and operator connections share endpoint validation and TLS construction. |
| Fresh state | Add `database/tenant/current-state.sql` and `tests/WorkerDock.IntegrationTests/TenantSnapshotTests.cs`; use `SqlDatabaseFixture` to compare it with the full manifest chain. |
| Deployment tooling removal | Remove the six lifecycle/package `.ps1` files and `deploy/WorkerDock.Deployment.psm1`; move `deploy/config/{tenant,worker,center}.example.json` to `examples/config/` and `deploy/permissions*.sql` to `database/tenant/`; replace `tests/DeploymentSmoke.ps1` with source-example/script smoke checks; remove `tests/deploy/*.Tests.ps1` and `deploy/config/migrator.example.json`. |
| Operator guidance | Update `README.md`, current `docs/how-to`, `docs/reference`, tutorial, and validation pages that present removed commands as required; retain dated specs/plans as historical records. |

Task 1 precedes Tasks 2 and 3. Tasks 2, 3, and 4 can be reviewed independently after Task 1. Task 5 follows them.

---

### Task 1: Relocate and harden the incremental migration engine

**Files:**
- Move: `src/WorkerDock.DatabaseMigrator/MigrationRunner.cs` -> `src/WorkerDock.Infrastructure/Migrations/MigrationRunner.cs`
- Move: `src/WorkerDock.DatabaseMigrator/SqlServerPrerequisites.cs` -> `src/WorkerDock.Infrastructure/Migrations/SqlServerPrerequisites.cs`
- Move: `src/WorkerDock.DatabaseMigrator/LegacyTemplateRemovalPreflight.cs` -> `src/WorkerDock.Infrastructure/Migrations/LegacyTemplateRemovalPreflight.cs`
- Delete: `src/WorkerDock.DatabaseMigrator/{Program.cs,MigrationRunner.Upgrade.cs,WorkerDock.DatabaseMigrator.csproj}`
- Modify: `WorkerDock.slnx`, `tests/WorkerDock.IntegrationTests/{WorkerDock.IntegrationTests.csproj,MigrationInputTests.cs,MigrationTests.cs,MigrationPrerequisiteTests.cs,LegacyTemplateRemovalTests.cs}`
- Modify namespace imports in `tests/WorkerDock.IntegrationTests/{AlertDeliveryTests.cs,AttemptSqlTests.cs,CalendarPersistenceTests.cs,CapacityPolicySqlTests.cs,DynamicValuePersistenceTests.cs,ExecutionValueSnapshotSqlTests.cs,IntegrationSettingsTests.cs,JobLifecycleTests.cs,JobSqlIntegrationTests.cs,ProjectionSqlTests.cs,QuartzCapacityTests.cs,QuartzProcessFixture.cs,SqlProfileSqlTests.cs,TemplateCatalogSqlTests.cs,TenantSettingsTests.cs}`

**Interfaces:**
- Consumes: existing manifest and SQL files at `scriptsRoot/tenant/`.
- Produces: `WorkerDock.Infrastructure.Migrations.MigrationRunner(string connectionString, Guid instanceId, string scriptsRoot)` with `ValueTask ApplyAsync(string databaseKind, string targetMigration, CancellationToken ct)` and `ValueTask RollbackAsync(string databaseKind, string targetMigration, CancellationToken ct)`; `SqlServerPrerequisites.Validate(int majorVersion, int compatibilityLevel)`; `LegacyTemplateRemovalPreflight.ValidateAsync(SqlConnection, SqlTransaction, CancellationToken)`.

- [ ] **Step 1: Write failing tests.** In `MigrationTests.cs`, add `History_hole_or_unknown_row_blocks_next_migration_before_write`, `Wrong_instance_blocks_next_migration_before_write`, and `Target_behind_installed_history_is_rejected`. Each records the history before calling `ApplyAsync`, then asserts `Assert.Equal(expectedCode, error.Code)` and `Assert.Equal(before, await ReadHistoryAsync(database))`; the expected codes are `InvalidMigrationHistory`, `TenantInstanceMismatch`, and `MigrationTargetBehindCurrent`. Keep the existing checksum-tamper case.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~MigrationTests|FullyQualifiedName~MigrationInputTests'` must show the new history/target assertions fail on a disposable SQL fixture; without `WORKERDOCK_TEST_SQL`, record `SQL_TEST_BLOCKED` and run only the non-SQL input tests as the local gate.
- [ ] **Step 3: Remove obsolete API tests.** Delete the CLI/backup/package-only cases in `MigrationInputTests.cs` and `MigrationTests.cs`; retain checksum, prerequisite, rollback, and legacy-template cases. Run the non-SQL `MigrationInputTests` filter; expect it to pass.
- [ ] **Step 4: Relocate the engine.** Move the three reusable source files to `WorkerDock.Infrastructure.Migrations`; delete `Program.cs`, `MigrationRunner.Upgrade.cs`, and the executable project; update the listed test imports and project/solution references. Run `dotnet build WorkerDock.slnx -c Release`; expect no missing project or namespace references.
- [ ] **Step 5: Implement prevalidation.** Move `ValidateUpgradeHistoryAsync`'s contiguous-prefix and dual-checksum logic into `MigrationRunner` as `private Task ValidateAppliedHistoryAsync(SqlConnection connection, int targetIndex, CancellationToken ct)`. Hold a session `WorkerDock.Schema` application lock through history validation and all target migrations; release it in `finally`. Reject an existing partial WorkerDock schema, unknown/gapped history, wrong instance, and a target older than the installed last entry before mutation. Keep per-migration serializable transactions and the `0010_remove_builtin_v1` preflight.
- [ ] **Step 6: Run green.** Run the Step 2 command plus `dotnet test tests/WorkerDock.UnitTests -c Release`; expect new focused tests and existing migration tests to pass when SQL is configured, and all non-SQL tests to pass otherwise. Do not mark SQL tests passed without the fixture.
- [ ] **Step 7: Commit only Task 1 files.** `git add` the listed migration, solution, and test paths, inspect `git diff --cached --name-only`, then `git commit -m "refactor: move tenant migration engine into infrastructure"`.

### Task 2: Add the explicit PowerShell migration command

**Files:**
- Create: `src/WorkerDock.Infrastructure/Persistence/TenantSqlConnectionBuilder.cs`, `scripts/Invoke-TenantMigration.ps1`, `tests/scripts/Invoke-TenantMigration.Tests.ps1`
- Modify: `src/WorkerDock.Infrastructure/Persistence/{TenantSqlConfiguration.cs,TenantSqlConnectionResolver.cs}`, `tests/WorkerDock.UnitTests/TenantSqlConnectionTests.cs`

**Interfaces:**
- Consumes: Task 1 `MigrationRunner.ApplyAsync` and `RollbackAsync`.
- Produces: `TenantSqlConnectionBuilder.BuildWithPassword(string host, int port, string database, string user, string password): string`; `TenantSqlConfiguration.ValidateEndpoint(string host, int port, string database, string user): void`; PowerShell parameters `-Action Apply|Rollback -SqlHost <string> -Port <int> -Database <string> -Credential <PSCredential> -InstanceId <Guid> -TargetMigration <string> -ScriptsRoot <path> -AssemblyDirectory <published-web-path>`.

- [ ] **Step 1: Write failing builder tests.** In `TenantSqlConnectionTests.cs`, parse `BuildWithPassword("sql.example.test", 1433, "tenant", "migration_user", "secret")` with `SqlConnectionStringBuilder`; assert `DataSource == "tcp:sql.example.test,1433"`, `Encrypt == Mandatory`, `TrustServerCertificate == false`, `PersistSecurityInfo == false`, and SQL login `migration_user`. Assert malformed host/port/database/user and empty password throw `InvalidOperationException` with message `SqlConfigurationInvalid`.
- [ ] **Step 2: Run builder red.** `dotnet test tests/WorkerDock.UnitTests -c Release --filter FullyQualifiedName~TenantSqlConnectionTests`; expect a missing method or failed assertions.
- [ ] **Step 3: Implement builder.** Add `ValidateEndpoint` and `BuildWithPassword`; make `TenantSqlConnectionResolver` call the builder after its existing pre-vault validation so runtime TLS behavior remains identical.
- [ ] **Step 4: Run builder green.** Repeat the Step 2 command; expect all `TenantSqlConnectionTests` to pass.
- [ ] **Step 5: Write failing script tests.** In `tests/scripts/Invoke-TenantMigration.Tests.ps1`, parse the script and invoke invalid host/port, missing assembly files, and `[pscredential]::Empty`; assert nonzero exit before SQL access and no `SENTINEL-password` in output.
- [ ] **Step 6: Run script red.** `pwsh -NoProfile -File tests/scripts/Invoke-TenantMigration.Tests.ps1`; expect missing script or parameter behavior failures.
- [ ] **Step 7: Implement script.** Validate paths and parameters before extracting `PSCredential.GetNetworkCredential().Password`; load dependencies from a `dotnet publish src/WorkerDock.Web` output with an `AssemblyDependencyResolver`; call Task 1's runner synchronously through PowerShell; write only stable nonsecret error codes. Never call deployment scripts or start services.
- [ ] **Step 8: Run script green.** Repeat Step 6. With `WORKERDOCK_TEST_SQL`, also invoke the script against a disposable database to prove assembly loading, certificate validation, and `0001_bootstrap` apply; without it report `SQL_TEST_BLOCKED` for the live command.
- [ ] **Step 9: Commit only Task 2 files.** Inspect the staged diff, then `git commit -m "feat: add manual tenant migration script"`.

### Task 3: Supply a fresh-database desired-state snapshot

**Files:**
- Create: `database/tenant/current-state.sql`, `tests/WorkerDock.IntegrationTests/TenantSnapshotTests.cs`
- Modify: `tests/WorkerDock.IntegrationTests/SqlDatabaseFixture.cs` only if a reusable batch executor is needed.

**Interfaces:**
- Consumes: Task 1 runner, `database/tenant/manifest.json` (current final ID `0010_remove_builtin_v1`), `SqlDatabaseFixture`.
- Produces: one plain SQL file with a required `__WORKERDOCK_INSTANCE_ID__` token; its rendered form defines the full current schema, static seed rows, `TenantSettings`, `SchemaHistory` rows for every manifest entry, and the runtime role/grants. Operator-specific login-to-database-user mapping is included by the developer in that same rendered deployment action.

- [ ] **Step 1: Write failing tests.** `TenantSnapshotTests.Snapshot_matches_full_migration_chain` provisions two empty disposable databases, applies the last manifest ID to one through the runner, renders and executes the snapshot on the other, and compares normalized `sys.objects`, columns, indexes, constraints, SQL module definitions, grants, required static rows, tenant GUID, and ordered history IDs/checksums (ignoring timestamps and system IDs). Add `Unrendered_or_nonempty_snapshot_fails_before_creating_objects` and four `Snapshot_drift_is_detected` variants that separately change a grant, trigger, seed row, and history checksum.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~TenantSnapshotTests` must fail because the snapshot is absent or incomplete. If `WORKERDOCK_TEST_SQL` is absent, record `SQL_TEST_BLOCKED`; do not substitute a static parse for parity.
- [ ] **Step 3: Implement.** Apply the manifest chain to an empty disposable database and script its resulting user schema, grants, and static rows into consolidated final-state SQL; review the output rather than concatenating historical upgrade steps. Put invalid GUID and nonempty-database guards before the first DDL batch; use the rendered GUID for `TenantSettings`. Include role/grants and manifest-aligned history values whose semantics are equivalent-state baseline. Ensure the final state includes the result of `0010_remove_builtin_v1` (no built-in 1.0.0 catalog rows). Do not edit historical migration files to make parity pass.
- [ ] **Step 4: Run green.** Run the Step 2 command against a disposable SQL Server, both with snapshot isolation on and off where the fixture supports it. Expect parity and guards to pass. Missing SQL remains a blocker to claiming snapshot readiness.
- [ ] **Step 5: Commit only Task 3 files.** Inspect the staged diff, then `git commit -m "feat: add tenant SQL desired-state snapshot"`.

### Task 4: Remove prescribed deployment and package tooling

**Files:**
- Delete: `deploy/{Install-Tenant.ps1,Install-Worker.ps1,Install-Center.ps1,Upgrade-Instance.ps1,Uninstall-Instance.ps1,Build-Packages.ps1,WorkerDock.Deployment.psm1,config/migrator.example.json}` and `tests/deploy/{tenant-sql-vault.Tests.ps1,package-contents.Tests.ps1,center-storage.Tests.ps1}`
- Move: `deploy/config/{tenant,worker,center}.example.json` -> `examples/config/`
- Move: `deploy/{permissions.sql,permissions.rollback.sql}` -> `database/tenant/`
- Replace: `tests/DeploymentSmoke.ps1`

**Interfaces:**
- Consumes: Task 2 migration script; current tenant/worker/center JSON examples.
- Produces: source examples with no installer-owned path, a smoke command that validates those examples and parses the optional migration script, and no custom WorkerDock ZIP/package dependency.

- [ ] **Step 1: Write failing smoke checks.** Replace `tests/DeploymentSmoke.ps1` assertions with JSON-shape checks for the three examples, shared tenant/worker runtime password reference, valid `Tenant:Sql` fields, and PowerShell parse checks for `scripts/Invoke-TenantMigration.ps1`. Assert the removed deployment script paths and `WorkerDock.DatabaseMigrator.csproj` do not exist.
- [ ] **Step 2: Run red.** `pwsh -NoProfile -File tests/DeploymentSmoke.ps1` must fail while the old scripts and config locations remain.
- [ ] **Step 3: Remove and move.** Delete only the listed deployment/package scripts and their dedicated tests. Move examples and permission SQL without changing the runtime JSON values or SQL grants. Remove the migrator JSON example because the script accepts `PSCredential` and command parameters. Keep unrelated maintainer/load scripts.
- [ ] **Step 4: Run green.** `pwsh -NoProfile -File tests/DeploymentSmoke.ps1` and `dotnet build WorkerDock.slnx -c Release`; expect both to pass. Confirm `rg -n 'WorkerDock.DatabaseMigrator|Upgrade-Instance|Build-Packages|Install-Tenant' src scripts tests README.md docs/how-to docs/reference` shows only references Task 5 will update, not executable source calls.
- [ ] **Step 5: Commit only Task 4 files.** Inspect the staged diff, then `git commit -m "refactor: remove prescribed deployment tooling"`.

### Task 5: Rewrite current operator documentation

**Files:**
- Modify: `README.md`, `docs/how-to/{deploy-production.md,setup-sql-server.md,upgrade-and-restore.md,configure-tenants.md,manage-retention-and-archives.md}`, `docs/tutorials/run-with-sql-server.md`, `docs/reference/{deployment-model.md,database-objects.md,configuration.md}`, `docs/validation/{release-checklist.md,release-evidence.md}`
- Modify: `tests/WorkerDock.IntegrationTests/MigrationInputTests.cs` documentation assertions.

**Interfaces:**
- Consumes: Tasks 2–4 script, snapshot, and relocated examples/permission SQL.
- Produces: tool-neutral manual deployment, migration, snapshot, backup/restore, and readiness guidance.

- [ ] **Step 1: Write failing documentation assertions.** In `MigrationInputTests.cs`, assert the SQL setup guide names `scripts/Invoke-TenantMigration.ps1`, `database/tenant/current-state.sql`, and the fresh-only snapshot restriction; assert upgrade guidance orders operator backup before explicit migration and documents `0010_remove_builtin_v1` preflight/recovery. Assert current guides do not command `Upgrade-Instance.ps1`, `Build-Packages.ps1`, or `WorkerDock.DatabaseMigrator`.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~MigrationInputTests`; expect the new doc assertions to fail.
- [ ] **Step 3: Rewrite current guides.** Explain required binaries/configuration, one tenant database, runtime vault/TLS settings, two SQL preparation paths, explicit target selection, developer-selected build/deploy tooling, operator backup/stop/start/readiness/recovery sequence, and the Control Center's file-state behavior. Use `dotnet publish` and frontend build only as examples; mark Windows/IIS details as optional examples. Update paths to `examples/config/` and `database/tenant/permissions.sql`; remove custom ZIP/installer/migrator commands. Do not rewrite dated specs/plans as if they were current operator guidance.
- [ ] **Step 4: Run green.** Run Step 2 plus `pwsh -NoProfile -File tests/DeploymentSmoke.ps1`; inspect `rg -n 'Upgrade-Instance|Install-Tenant|Build-Packages|WorkerDock.DatabaseMigrator|deploy/config' README.md docs/how-to docs/reference docs/tutorials` and resolve active-guide references. Historical maintenance notes may retain their dated record.
- [ ] **Step 5: Commit only Task 5 files.** Inspect the staged diff, then `git commit -m "docs: describe manual deployment and database state paths"`.

## Final verification

Run `dotnet build WorkerDock.slnx -c Release`, UnitTests, focused migration and startup IntegrationTests, `pwsh -NoProfile -File tests/scripts/Invoke-TenantMigration.Tests.ps1`, and `pwsh -NoProfile -File tests/DeploymentSmoke.ps1`. Run the full SQL migration and snapshot parity tests on a disposable SQL Server before claiming database readiness. Run `git diff --check` on the implementation range and review the branch against the spec, including preservation of pre-existing dirty changes. Report every blocked fixture or unrelated baseline failure explicitly; do not call it a pass.
