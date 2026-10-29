# Direct Runtime-User Grants Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provision a tenant schema by granting its existing mapped database user the runtime permissions, without creating a role, database user, or server login, while supporting generated and reused tenant GUIDs.

**Architecture:** Keep one checksummed fresh-install baseline and its manual current-state snapshot. Pass and validate the existing runtime database principal in the migration runner, grant the existing least-privilege inventory directly to it, and remove role-membership helpers. The migration command gets mutually exclusive initial-setup and existing-instance modes; snapshot users render the same existing user and GUID into the SQL file.

**Tech Stack:** C#/.NET 10, SQL Server 2022+ (compatibility level 160), PowerShell 7 on .NET 10+, xUnit, Pester.

**Spec:** `docs/superpowers/specs/2026-10-27-direct-runtime-user-grants-design.md`

## Global Constraints

- Tenant SQL requires SQL Server 2022+ and compatibility level 160.
- Accept existing SQL-authenticated and Windows-authenticated database users only; reject roles and other principal types.
- Accept principal names from 1 to 128 characters, reject null/empty/whitespace-only values, and delimit names with `QUOTENAME` before using them as SQL identifiers.
- Setup scripts must not issue `CREATE ROLE`, `CREATE USER`, or `CREATE LOGIN`.
- Preserve the current per-object and per-column permission grants, procedure grants, and explicit archive delete denials.
- Do not add a persistent database object for permission management.
- Initial setup targets a fresh database, generates a new GUID, and displays it only after success; reuse mode requires the tenant's existing GUID.
- The snapshot remains a manually rendered fresh-database script; it cannot upgrade an existing database.

## Review Focus

- Missing or unsupported runtime principal must fail before any schema change; cover in Task 1.
- SQL identifier quoting must support a valid user name containing `]` without injection; cover in Task 2.
- Unrendered/invalid snapshot principal or GUID and non-fresh database must fail before schema mutation; cover in Task 2.
- Initial setup must reject a non-empty target; reuse mode must preserve/verify the existing GUID, and rollback must require that GUID; cover in Tasks 1 and 3.
- A repeated apply with the same tenant GUID is safe, while a mismatched GUID fails without changing the database; cover in Task 3.

---

## File Structure

- `src/WorkerDock.Infrastructure/Migrations/MigrationRunner.cs`: runtime principal and tenant GUID validation; binds migration parameters.
- `scripts/Invoke-TenantMigration.ps1`: input modes, generated GUID output, runtime principal input.
- `database/tenant/0001_initial.up.sql`, `0001_initial.down.sql`, `current-state.sql`, and `manifest.json`: direct permission grants, no created role, matching baseline/snapshot state, updated checksum.
- `database/tenant/permissions.sql` and `permissions.rollback.sql`: remove obsolete membership-only scripts; setup paths own grants and rollback.
- `tests/WorkerDock.IntegrationTests`: disposable mapped users, migration/snapshot parity, grant coverage, no-role and rollback assertions.
- `tests/scripts/Invoke-TenantMigration.Tests.ps1`: PowerShell parsing and setup-mode validation.
- Current SQL setup, deployment, configuration, database-object, migration, and SQL tutorial docs: explain the existing-user prerequisite and GUID modes.

## Tasks

### Task 1: Require and validate the existing runtime database user

**Files:**
- Modify: `src/WorkerDock.Infrastructure/Migrations/MigrationRunner.cs`
- Modify: `tests/WorkerDock.IntegrationTests/SqlDatabaseFixture.cs`, `TenantSqlFixture.cs`
- Modify constructor callers: `AttemptSqlTests.cs`, `CapacityPolicySqlTests.cs`, `DynamicValuePersistenceTests.cs`, `JobSqlIntegrationTests.cs`, `MigrationInputTests.cs`, `MigrationTests.cs`, `TenantSettingsTests.cs`, `TenantSnapshotTests.cs`, `TenantSqlWebStartupTests.cs`, `TemplateCatalogSqlTests.cs`
- Test: `tests/WorkerDock.IntegrationTests/MigrationInputTests.cs`, `MigrationTests.cs`

**Interfaces:**
- Produces: `MigrationRunner(string connectionString, Guid instanceId, string runtimePrincipal, string scriptsRoot)`.
- Produces: `SqlDatabaseFixture.CreateAsync(bool snapshot, string? runtimeLoginName = null)`; it creates a test-only database principal before migration, using `WITHOUT LOGIN` by default or mapping the supplied test login. Expose the selected principal as `RuntimePrincipal`; all test runners use it.
- `TenantSqlFixture` passes its configured runtime login as the fixture principal before applying the baseline; it no longer adds role membership afterward.

- [ ] **Step 1: Write failing SQL integration tests** named `Migration_rejects_missing_runtime_principal_before_creating_schema`, `Migration_rejects_role_as_runtime_principal_before_creating_schema`, and `Migration_rejects_nonempty_database_before_creating_schema`. Assert the appropriate error codes and that `SCHEMA_ID(N'workerdock')` remains null; preserve any sentinel table.
- [ ] **Step 2: Run the tests and verify they fail** because `MigrationRunner` has no runtime-principal input or validation.

Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~Migration_rejects_missing_runtime_principal|FullyQualifiedName~Migration_rejects_role_as_runtime_principal|FullyQualifiedName~Migration_rejects_nonempty_database"`

Expected: FAIL; SQL test connection must be set through `WORKERDOCK_TEST_SQL`.

- [ ] **Step 3: Add the constructor parameter and validate before migration execution.** Query `sys.database_principals` with a SQL parameter; accept only `S` and `U`, reject an absent principal, role, or other principal with `RuntimePrincipalInvalid`. When the baseline is unapplied, reject any user objects other than the supplied database user with `TenantDatabaseNotEmpty` before executing a batch. Bind `@RuntimePrincipal` in each `ExecuteScriptAsync` batch. Update the fixture and all constructor call sites to pass the pre-created test principal.
- [ ] **Step 4: Run `MigrationInputTests` and the two new SQL tests.**

Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~MigrationInputTests|FullyQualifiedName~Migration_rejects_missing_runtime_principal|FullyQualifiedName~Migration_rejects_role_as_runtime_principal|FullyQualifiedName~Migration_rejects_nonempty_database"`

Expected: PASS; invalid identities fail before tenant objects are created.

- [ ] **Step 5: Commit** as `feat: require an existing tenant runtime user`.

### Task 2: Grant the baseline permissions directly and remove the role

**Files:**
- Modify: `database/tenant/0001_initial.up.sql`, `0001_initial.down.sql`, `current-state.sql`, `manifest.json`
- Delete: `database/tenant/permissions.sql`, `permissions.rollback.sql`
- Modify: `tests/WorkerDock.IntegrationTests/CustomSqlSchemaTests.cs`, `TenantSnapshotTests.cs`, `ObservabilityTests.cs`

**Interfaces:**
- Consumes: `MigrationRunner` binds `@RuntimePrincipal` and validates the existing database user.
- Produces: both baseline paths grant the existing per-object permissions and explicit denials to the same supplied database user; neither creates a role or user.

- [ ] **Step 1: Write failing assertions** that `0001_initial.up.sql` and `current-state.sql` contain no `CREATE ROLE`, `CREATE USER`, or grants to `WorkerDockRuntime`; preserve the existing AuditEvents and ArchiveBatches/ArchiveBatchItems restrictions; verify the snapshot inserts one `SchemaHistory` row matching the manifest checksums; reject an unrendered principal, invalid GUID, and non-fresh database before mutation; and verify migration/snapshot permissions match for the supplied principal. Add a SQL integration case with a valid principal containing `]` and assert its grants resolve to that user.
- [ ] **Step 2: Run the focused tests and verify they fail** against the current role-based grants.

Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~Fresh_baseline_permissions|FullyQualifiedName~Snapshot_matches_full_migration_chain"`

Expected: FAIL because baseline permissions still target `WorkerDockRuntime` and the snapshot history row is missing.

- [ ] **Step 3: Replace role grants with a matching direct-grant block at the end of each baseline path.** Preserve every existing permission and denial, compose the principal identifier with `QUOTENAME`, and use the bound `@RuntimePrincipal` in the migration. In the snapshot, accept `__WORKERDOCK_RUNTIME_PRINCIPAL__` and validate it before schema creation. Remove `CREATE ROLE` and `DROP ROLE`; retain the existing rollback guard and verify dropping the tenant schema leaves the supplied user intact and without grants on dropped objects. Insert a `SchemaHistory` row in the snapshot with the same `0001_initial` up/down checksums recorded in `manifest.json`, so applying the migration runner to a completed snapshot is an idempotent no-op. Remove the obsolete membership scripts. Recalculate both baseline checksums in `manifest.json` and update the snapshot history values.
- [ ] **Step 4: Run snapshot parity, validation, permission, and rollback tests.** Update `TenantSnapshotTests` to create the principal before running either path and compare the complete grants/denies assigned to it. Assert `WorkerDockRuntime` does not exist after either setup path and that `SchemaHistory` contains the matching satisfied baseline row.

Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~TenantSnapshotTests|FullyQualifiedName~Fresh_baseline_permissions|FullyQualifiedName~FreshInstall_HasNoQuartzObjects"`

Expected: PASS; both setup paths produce matching permissions without a role, and rollback retains the existing principal.

- [ ] **Step 5: Commit** as `refactor: grant tenant permissions to existing user`.

### Task 3: Add initial-setup and existing-GUID modes

**Files:**
- Modify: `scripts/Invoke-TenantMigration.ps1`
- Test: `tests/scripts/Invoke-TenantMigration.Tests.ps1`, `tests/WorkerDock.IntegrationTests/MigrationTests.cs`

**Interfaces:**
- Produces: `-InitialSetup` switch for first apply, with no `-InstanceId`; `-InstanceId <guid>` for reuse and all rollback operations; required `-RuntimePrincipal <database-user>` for both actions.
- On successful initial apply, stdout displays the generated GUID after the migration runner returns. No generated GUID is reported on a failed migration.

- [ ] **Step 1: Add Pester cases** for initial apply without a supplied ID, existing apply with a valid ID, absent ID, malformed ID, both modes together, `-InitialSetup` with rollback, and failed initial apply not printing a generated ID. Retain the SQL `MigrationTests` coverage that same-ID apply is a no-op and a different ID is rejected.
- [ ] **Step 2: Run the script tests and verify new mode cases fail.**

Run: `pwsh -NoProfile -File tests/scripts/Invoke-TenantMigration.Tests.ps1`

Expected: FAIL for the new cases because the script has no initial-setup mode or runtime-principal parameter.

- [ ] **Step 3: Implement the modes and pass `RuntimePrincipal` to the `MigrationRunner`.** Generate `[guid]::NewGuid()` only for `Action=Apply` with `-InitialSetup`; otherwise require a parseable `-InstanceId`. Reject mutually supplied modes and initial setup for rollback. Rely on the runner fresh-database guard; on an already-applied database the supplied/generated GUID must match the stored `TenantSettings.InstanceId`. Emit the generated GUID only after the awaited apply completes successfully.
- [ ] **Step 4: Run the Pester tests and deployment smoke parser.**

Run: `pwsh -NoProfile -File tests/scripts/Invoke-TenantMigration.Tests.ps1` and `pwsh -NoProfile -File tests/DeploymentSmoke.ps1`

Expected: PASS; invalid modes fail with safe error codes, and valid inputs reach the runner.

- [ ] **Step 5: Commit** as `feat: support generated tenant instance ids`.

### Task 4: Align setup guidance and verify all references

**Files:**
- Modify current docs: `docs/how-to/setup-sql-server.md`, `deploy-production.md`, `upgrade-and-restore.md`, `configure-tenants.md`; `docs/tutorials/run-with-sql-server.md`; `docs/reference/database-objects.md`, `configuration.md`, `database-migrations.md`, `deployment-model.md`, `observability.md`; `tests/WorkerDock.IntegrationTests/MigrationInputTests.cs`
- Do not rewrite historical specs/plans that describe the prior design.

- [ ] **Step 1: Add documentation assertions** for the existing database-user prerequisite, `-RuntimePrincipal`, `-InitialSetup`, existing `-InstanceId` reuse, and absence of the old role-membership flow. Add assertions before changing the current docs.
- [ ] **Step 2: Run the documentation contract test and verify it fails** on stale role and GUID instructions.

Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~Current_database_guides_document_manual_migration_and_fresh_snapshot_paths"`

Expected: FAIL while docs still describe role membership, missing principal creation, or lack GUID modes.

- [ ] **Step 3: Update current guidance.** State that the selected database user must already exist in the target database and scripts only validate/grant it. Document generating one GUID with `-InitialSetup`, saving the successful output for tenant configuration, and supplying the same GUID with `-InstanceId` for reuse. For manual snapshot use, document replacing every instance and runtime-principal token; use a fresh database and do not run the migration source directly.
- [ ] **Step 4: Run focused docs tests, non-SQL integration tests, checksum validation, and `git diff --check`.**

Run: `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~Current_database_guides_document_manual_migration_and_fresh_snapshot_paths|FullyQualifiedName~Tenant_manifest_checksums_match_every_up_and_down_script"`; run `git diff --check`.

Expected: PASS; no active setup instructions create a user or role, or use the deleted membership scripts.

- [ ] **Step 5: Commit** as `docs: document direct grants and tenant guid modes`.

## Execution notes

- The SQL integration tests require `WORKERDOCK_TEST_SQL` pointing to a disposable SQL Server 2022+ instance. If unavailable, run the focused static and PowerShell tests and report SQL runtime validation as unverified; do not claim the SQL integration tests passed.
- Keep commits scoped to the files listed for each task; preserve unrelated working-tree changes.