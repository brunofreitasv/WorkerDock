# Repository Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the unused SQL-backed Control Center transition path and historical repository clutter while preserving current tenant migrations, the file-backed center, and user work.

**Architecture:** Retire the central schema, importer, and central migrator mode together because they depend on each other. Limit copied SQL to tenant migrations, verify the produced packages, then prune superseded documents and tracked work logs. Git history preserves removed material.

**Tech Stack:** .NET 10/C#, SQL Server 2022+, xUnit, PowerShell 7, React/Vite, Git.

**Spec:** [Repository cleanup design](repository-cleanup-design.md).

## Global Constraints

- There are no SQL-backed Control Center installations to migrate; retiring the importer does not discard deployed data.
- Keep every `database/tenant/manifest.json` entry and the exact bytes of its paired `.up.sql` and `.down.sql` scripts.
- Keep test-only SQL under `tests/sql` and `tests/WorkerDock.IntegrationTests/Sql`. Third-party scheduler source and its notice were removed with the single-runtime cutover.
- Current Control Center persistence stays in protected files; its package and upgrade path require no SQL.
- Preserve all unrelated uncommitted logo edits and the modified `.superpowers/sdd/2026-10-17-workerdock-03-templates/progress.md` on disk.
- Source cleanup does not delete ignored local build output, SQL databases, backup archives, or deployed installations.

## Review Focus

- `apply central 0001_registry` with any connection string must fail as `InvalidDatabaseKind` before opening SQL (Task 1 test).
- A tenant migrator build must still include the checksummed tenant manifest and every paired SQL script (Task 2 package test).
- Tenant and center packages must contain no archived central or upstream SQL; the center must contain no migrator (Task 2 package test).
- Removing tracked work logs must leave the user's modified local progress file and logo assets intact (Task 3 preservation check).
- Active docs must contain no command or link to the removed importer, central schema, or historical plans (Task 3 reference scan).

---

## File map

- `src/WorkerDock.DatabaseMigrator/{Program.cs,MigrationRunner.cs,WorkerDock.DatabaseMigrator.csproj}` owns current tenant migration CLI, validation, and packaged SQL assets.
- `database/tenant/` owns immutable current runtime SQL; `database/central/` and `src/WorkerDock.CentralImport/` are retired together.
- `src/WorkerDock.ControlCenter/Registry/CentralJournalBootstrap.cs` contains importer-only DTOs and bootstrap logic; the other journal/registry files stay.
- `tests/WorkerDock.IntegrationTests/` contains migration contracts and SQL fixtures; remove only central migration/import tests and narrow its content copy.
- `deploy/Upgrade-Instance.ps1` owns tenant SQL and center file upgrade branches; `tests/deploy/package-contents.Tests.ps1` will check real package entries.
- `README.md` and active `docs/` pages explain current setup. Historical `docs/superpowers` files and tracked `.superpowers/sdd` logs are removed from the published tree, while this spec and plan remain.

### Task 1: Retire central SQL and importer as one unit

**Files:**
- Modify: `src/WorkerDock.DatabaseMigrator/Program.cs`, `src/WorkerDock.DatabaseMigrator/MigrationRunner.cs`, `WorkerDock.slnx`.
- Modify: `tests/WorkerDock.UnitTests/WorkerDock.UnitTests.csproj`, `tests/WorkerDock.UnitTests/packages.lock.json`, `tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj`, `tests/WorkerDock.IntegrationTests/packages.lock.json`.
- Delete: `database/central/0001_registry.up.sql`, `database/central/0001_registry.down.sql`, `database/central/manifest.json`, all files under `src/WorkerDock.CentralImport/`, `src/WorkerDock.ControlCenter/Registry/CentralJournalBootstrap.cs`.
- Delete: `tests/WorkerDock.IntegrationTests/CentralMigrationContractTests.cs`, `CentralMigrationSqlTests.cs`, `CentralLegacyImportSqlTests.cs`; `tests/WorkerDock.UnitTests/CentralImportCommandTests.cs`, `CentralJournalBootstrapTests.cs`.
- Test: `tests/WorkerDock.IntegrationTests/MigrationInputTests.cs`.

**Interfaces:**
- Consumes: existing `MigrationRunner(string connectionString, Guid instanceId, string scriptsRoot)` and current tenant manifest.
- Produces: unchanged `ApplyAsync(string databaseKind, string targetMigration, CancellationToken ct)` and `RollbackAsync(string databaseKind, string targetMigration, CancellationToken ct)` signatures; only `databaseKind == "tenant"` is valid.

- [ ] **Step 1: Write the failing test.** Add `Central_database_kind_is_rejected_before_connecting` to `MigrationInputTests`: construct `MigrationRunner("invalid connection", Guid.NewGuid(), Path.Combine(AppContext.BaseDirectory, "database"))`, call `ApplyAsync("central", "0001_registry", default)`, and assert `WorkerDockException.Code == "InvalidDatabaseKind"`.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests --filter FullyQualifiedName~Central_database_kind_is_rejected_before_connecting --no-restore`; expect failure because the current runner accepts `central` and attempts legacy migration work.
- [ ] **Step 3: Remove the retired path.** Make `MigrationRunner.RunAsync` reject any kind except `tenant` before file or SQL access; remove central identity branching from `ApplyOneAsync` and `CheckInstanceAsync`, and change CLI usage to `apply|rollback tenant <migration>`. Remove the listed importer/schema/tests/projects and their solution/project references without changing tenant migration files.
- [ ] **Step 4: Refresh lock files.** Run `dotnet restore WorkerDock.slnx`; expect restore success and lock files updated for removed project references.
- [ ] **Step 5: Run green.** Run the focused test, `dotnet build WorkerDock.slnx --no-restore`, and `dotnet test tests/WorkerDock.UnitTests --no-restore`; expect zero failures and no importer project in the solution.
- [ ] **Step 6: Commit.** Stage only Task 1 paths and commit `refactor: retire central SQL import path`.

### Task 2: Keep packages and upgrades tenant-only for SQL

**Files:**
- Modify: `src/WorkerDock.DatabaseMigrator/WorkerDock.DatabaseMigrator.csproj`, `tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj`, `deploy/Upgrade-Instance.ps1`.
- Create: `tests/deploy/package-contents.Tests.ps1`.
- Test: `tests/DeploymentSmoke.ps1`, `tests/deploy/center-storage.Tests.ps1`.

**Interfaces:**
- Consumes: Task 1's tenant-only `MigrationRunner` and `database/tenant/manifest.json`.
- Produces: `package-contents.Tests.ps1 -TenantPackagePath <zip> -CenterPackagePath <zip>` which fails unless the tenant ZIP contains `tools/database-migrator/database/tenant/manifest.json` and all manifest-listed up/down scripts, neither ZIP contains `/database/central/` or `/database/upstream/`, and the center ZIP contains no database migrator.

- [ ] **Step 1: Write the failing package test.** Implement the script's two mandatory path parameters and assert the listed ZIP-entry contract using `[IO.Compression.ZipFile]::OpenRead`; compare tenant script IDs with the manifest inside the ZIP.
- [ ] **Step 2: Run red against current packages.** Run `pwsh -NoProfile -File deploy/Build-Packages.ps1 -Version 0.1.0-cleanup-red -OutputRoot .\artifacts\cleanup-red`, then the new package test on the two ZIPs; expect failure because the broad migrator content copy includes `database/upstream`.
- [ ] **Step 3: Narrow copied content and dead upgrade code.** Change both `.csproj` content includes from `../../database/**/*` to `../../database/tenant/**/*`, keeping the linked `database/tenant/` path. In `Upgrade-Instance.ps1`, use tenant literals only after the center branch returns, and remove unreachable center SQL migration alternatives; keep center state backup and tenant SQL backup behavior intact.
- [ ] **Step 4: Run green.** Rebuild packages with version `0.1.0-cleanup-green` into `artifacts/cleanup-green`; run `package-contents.Tests.ps1` on both ZIPs, `tests/DeploymentSmoke.ps1`, and `tests/deploy/center-storage.Tests.ps1`. Expect all to exit 0.
- [ ] **Step 5: Commit.** Stage only Task 2 paths and commit `build: package only current tenant SQL`.

### Task 3: Remove stale records and update active docs

**Files:**
- Modify: `README.md`, `docs/index.md`, `docs/architecture.md`, `docs/control-center.md`, `docs/database.md`, `docs/how-to/setup-sql-server.md`, `docs/how-to/deploy-production.md`, `docs/how-to/configure-tenants.md`, `docs/reference/configuration.md`, `docs/reference/database-objects.md`, `deploy/permissions.sql`.
- Delete: `docs/superpowers/plans/2026-10-17-workerdock-01-foundation.md`, `2026-10-17-workerdock-02-execution.md`, `2026-10-17-workerdock-03-templates.md`, `2026-10-17-workerdock-04-scheduling-ui.md`, `2026-10-17-workerdock-05-operations-center.md`, `2026-10-17-workerdock-06-release.md`, `2026-10-17-workerdock-implementation.md`, `docs/superpowers/specs/2026-10-17-workerdock-design.md`, `docs/superpowers/plans/2026-10-19-database-free-control-center.md`, and `docs/superpowers/specs/2026-10-19-database-free-control-center-design.md`; keep this cleanup plan/spec.
- Untrack while preserving local files: tracked `.superpowers/sdd/**` reports and progress logs.

**Interfaces:**
- Consumes: Task 1's removal list and Task 2's verified package layout.
- Produces: active setup documentation with one supported SQL migration root, `database/tenant`, and no legacy importer procedure.

- [ ] **Step 1: Record preservation state.** Run `git status --short`, `git ls-files '.superpowers/sdd/**'`, and record the SHA-256 of the modified progress file and dirty logo files. Do not stage or delete those logo files.
- [ ] **Step 2: Update active docs.** Remove archived central schema/import instructions and links; retain fresh tenant schema, test fixture, center file-state, and release-validation guidance. Stage only the README SQL hunk so its pre-existing logo hunk remains unstaged.
- [ ] **Step 3: Remove historical records from tracking.** Delete the listed superseded `docs/superpowers` plan/spec files. After confirming the tracked list, run index-only removal for `.superpowers/sdd`; its ignored local files must remain intact.
- [ ] **Step 4: Verify.** Search active docs and code for `database/central`, `WorkerDock.CentralImport`, `apply central`, and `rollback central`; expect no references outside this cleanup spec/plan or retained Git history. Confirm the recorded local file hashes are unchanged, the deleted docs have no remaining active links, and `git diff --check` passes.
- [ ] **Step 5: Commit.** Stage only Task 3 changes and commit `docs: remove obsolete central SQL guidance and work logs`.

## Whole-branch verification

- [ ] Run `dotnet restore WorkerDock.slnx --locked-mode` and `dotnet build WorkerDock.slnx --no-restore`; expect success.
- [ ] Run `dotnet test WorkerDock.slnx --no-restore -c Release --filter "Store!=Sql"`; expect zero failures.
- [ ] Run both deployment smoke scripts and the Task 2 ZIP-content test on freshly built packages; expect `pass` or exit 0.
- [ ] Review the final diff against the removal list and confirm the unrelated logo changes and local progress file remain outside the cleanup commits.
- [ ] Record SQL-backed integration and live IIS deployment as unverified if their configured environments are unavailable; do not report them as passing.
