# Built-in Template 1.0.0 Removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate all SQL-backed HTTP and SQL Server job revisions to template 1.1.0 and remove built-in 1.0.0 catalog and runtime support.

**Architecture:** A preflight in the existing migrator validates the tenant's legacy revisions and verifies that no execution refers to them. A transactional tenant migration rewrites those revisions and removes old catalog references. The Web, Scheduler, Worker, Executor, template packages, and SQL profile diagnostic then expose only 1.1.0.

**Tech Stack:** .NET 10, C#, SQL Server tenant migrations, xUnit integration tests, existing template contracts and runtime hosts.

**Spec:** [Approved design](../specs/2026-10-20-template-removal-tenant-navigation-design.md), commit fbdbcfd.

## Global Constraints

- Work in C:\Users\bruno\.codex\worktrees\template-max-properties\WorkerDock 2 on branch codex/workerdock-final.
- Only built-in workerdock.http and workerdock.sql-server versions change; do not reject custom templates merely because their version is 1.0.0.
- Rewrite every stored built-in 1.0.0 JobRevisions row, including archived jobs and earlier revisions.
- Reject migration if any execution refers to a built-in 1.0.0 revision; the user reports no past executions.
- Preserve old migration files and checksums. Add 0010_remove_builtin_v1 to the tenant manifest.
- Require a stopped-service upgrade and verified database/release backup. The down migration refuses reversal.
- Leave Development Memory-mode 503 behavior unchanged.
- Never log configuration JSON, connection strings, secret references, or raw SQL exceptions.
- Tasks are sequential. Commit each reviewed, passing task; stage only its files.

## Review Focus

- A completed or pending execution referring to a 1.0.0 revision must block migration, with all rows unchanged. Test in Task 1.
- For a tenant with legacy revisions, a missing or wrong-digest 1.1.0 catalog entry must block migration before any legacy row is changed. Test in Task 1.
- An invalid 1.1.0 configuration must block migration without echoing its contents. Test in Task 1.
- Archived and historical job revisions must migrate, while a custom 1.0.0 template remains untouched. Test in Task 2.
- Old worker capabilities must disappear, and catalog metadata immutability must still reject mutation after migration. Test in Task 2.

---

## Execution and file map

Use the SQL fixture's isolated database through WORKERDOCK_TEST_SQL. A missing SQL prerequisite is a blocker, not a pass. Run each named test red before implementation and green afterward. Use `git -c safe.directory='C:/Users/bruno/.codex/worktrees/template-max-properties/WorkerDock 2'` for Git commands if the sandbox reports dubious ownership.

| Unit | Files and responsibility |
| --- | --- |
| Preflight | New src/WorkerDock.DatabaseMigrator/LegacyTemplateRemovalPreflight.cs validates 1.1 catalog, execution absence, config compatibility, and known foreign-key references. MigrationRunner.cs invokes it in the migration transaction in Task 2. |
| Data migration | New database/tenant/0010_remove_builtin_v1.up.sql and .down.sql; manifest.json registers their checksums. Old scripts remain byte-for-byte unchanged. |
| Template packages | SqlTemplate.cs, SqlTemplateDescriptors.cs, HttpTemplate.cs, HttpTemplateDescriptors.cs and built-in authoring validators contain only 1.1 behavior. |
| Composition | Web, Scheduler, Worker, Executor registration and SqlProfileDiagnostics use 1.1. Catalog API exposes no removed version. |
| Tests and docs | New LegacyTemplateRemovalTests.cs plus focused existing template/catalog/diagnostic tests; English and Portuguese help and upgrade guidance describe the migration. |

### Task 1: Fail-closed migration preflight

**Files:** Create src/WorkerDock.DatabaseMigrator/LegacyTemplateRemovalPreflight.cs and tests/WorkerDock.IntegrationTests/LegacyTemplateRemovalTests.cs.

**Interfaces:** Produce `public static ValueTask ValidateAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)`. Task 2 calls it from `MigrationRunner.ApplyOneAsync` for entry ID 0010_remove_builtin_v1, inside the migration transaction before executing the up script. Use safe WorkerDockException codes LegacyTemplateExecutionsPresent, LegacyTemplateCatalogUnavailable, LegacyTemplateConfigurationInvalid, and LegacyTemplateReferencePresent.

- [ ] **Step 1: Write failing SQL tests.** Name tests `Execution_reference_blocks_removal`, `Missing_or_mismatched_v11_catalog_blocks_removal`, and `Invalid_legacy_configuration_blocks_removal_without_leakage`. Seed the old catalog and job revision, add the named blocker, call ValidateAsync in a SQL transaction, assert the safe code and unchanged job version/catalog rows. The execution test covers terminal and nonterminal states. The invalid-config test uses a sentinel secret and asserts it is absent from the error.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~LegacyTemplateRemovalTests`; expect the new tests to fail because preflight is absent.
- [ ] **Step 3: Implement preflight.** When legacy job revisions exist, check both 1.1 catalog rows against trusted descriptor digests and nonnull descriptor metadata. A fresh database with no legacy job revisions may remove the old bootstrap rows before the worker registers 1.1. Reject any execution joined to a built-in 1.0 revision. Validate each old ConfigurationJson with its DynamicValuesJson and PolicyJson job timeout against the corresponding 1.1 descriptor using existing schema and semantic authoring validators. Reject foreign keys to TemplateCatalog other than JobRevisions and WorkerCapabilities. Do not include stored values in error messages.
- [ ] **Step 4: Run green.** Run the Task 1 command; expect all three named tests to pass when SQL is available.
- [ ] **Step 5: Commit.** Stage only Task 1 files, run `git diff --cached --check`, commit `test: guard built-in template migration`.

### Task 2: Rewrite and remove legacy database rows

**Files:** Create database/tenant/0010_remove_builtin_v1.up.sql and .down.sql. Modify database/tenant/manifest.json, src/WorkerDock.DatabaseMigrator/MigrationRunner.cs, tests/WorkerDock.IntegrationTests/LegacyTemplateRemovalTests.cs, and tests/WorkerDock.IntegrationTests/CustomSqlSchemaTests.cs.

**Interfaces:** MigrationRunner calls Task 1 preflight inside its transaction before the up script. The up script runs in MigrationRunner's serializable transaction after Task 1 preflight. The down script throws SQL error 51002, mapped by MigrationRunner to UnsafeRollback. Manifest hashes are SHA-256 of exact script bytes.

- [ ] **Step 1: Write failing migration tests.** Name tests `All_legacy_revisions_migrate_and_unrelated_templates_remain`, `Fresh_install_removes_bootstrap_v1_without_v11_catalog`, and `Legacy_catalog_and_capabilities_are_removed_with_guards_restored`. Seed current, historical, and archived built-in revisions plus a custom template whose version is 1.0.0; seed old WorkerCapabilities. Register valid 1.1 descriptors. Assert every built-in revision becomes 1.1.0, custom version stays 1.0.0, old catalog/capability rows are gone, and later unauthorized JobRevisions or catalog edits still fail. Assert second ApplyAsync is idempotent and RollbackAsync returns UnsafeRollback. Update schema-ID expectation and manifest checksum test.
- [ ] **Step 2: Run red.** Run the Task 1 test command and `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~CustomSqlSchemaTests`; expect failures for the absent migration.
- [ ] **Step 3: Implement SQL scripts and manifest.** Call Task 1 preflight from MigrationRunner before the script. In the up script disable only JobRevisions_Immutable and TemplateCatalog_Immutable, update matching built-in revisions, delete matching WorkerCapabilities, delete the two old catalog rows, then enable both triggers before the transaction can commit. Add SQL postconditions that no built-in 1.0 references remain. Keep old migration scripts untouched. Make down.sql fail closed and compute manifest hashes from final bytes.
- [ ] **Step 4: Run green.** Run both Task 2 commands plus `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~MigrationInputTests`; expect pass.
- [ ] **Step 5: Commit.** Stage only Task 2 files, run `git diff --cached --check`, commit `feat: migrate and remove built-in template v1 catalog`.

### Task 3: Remove legacy behavior from template packages

**Files:** Modify src/WorkerDock.Templates.SqlServer/SqlTemplate.cs, SqlTemplateDescriptors.cs, SqlConfigurationValidator.cs; src/WorkerDock.Templates.Http/HttpTemplate.cs, HttpTemplateDescriptors.cs, HttpConfigurationValidator.cs; tests/WorkerDock.IntegrationTests/SqlTemplateTests.cs, SqlTemplateAuthoringTests.cs, HttpTemplateTests.cs, SqlTemplateTargetTests.cs.

**Interfaces:** Default constructors and descriptor access resolve 1.1.0. Explicit Get("1.0.0") for built-in descriptors throws ArgumentOutOfRangeException. Fixed-value 1.1.0 execution remains supported.

- [ ] **Step 1: Write failing tests.** Assert default SQL and HTTP descriptors are 1.1.0, explicit built-in 1.0.0 descriptor lookup is rejected, SQL Date and DateTime conversion follows 1.1 semantics, and fixed HTTP parameters still execute under 1.1. Update existing tests that assumed a 1.0 default.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter 'FullyQualifiedName~SqlTemplateTests|FullyQualifiedName~SqlTemplateAuthoringTests|FullyQualifiedName~HttpTemplateTests'`; expect the new assertions to fail.
- [ ] **Step 3: Implement only 1.1 paths.** Make built-in defaults 1.1, remove legacy descriptor instances and version branches, and keep shared validation/execution rules. Avoid changing unrelated NuGet package version numbers.
- [ ] **Step 4: Run green.** Run Task 3 command and `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter FullyQualifiedName~SqlTemplateTargetTests`; expect pass where target SQL prerequisites are available.
- [ ] **Step 5: Commit.** Stage only Task 3 files, run `git diff --cached --check`, commit `refactor: support built-in template v1.1 only`.

### Task 4: Align hosts, authoring, catalog, and diagnostics

**Files:** Modify src/WorkerDock.Web/Program.cs; src/WorkerDock.Scheduler/Program.cs; src/WorkerDock.Worker/Program.cs; src/WorkerDock.Executor/Program.cs; src/WorkerDock.Infrastructure/Templates/BuiltInAuthoringValidators.cs and TemplateMetadata.cs; src/WorkerDock.Infrastructure/Connections/SqlProfileDiagnostics.cs; src/WorkerDock.Infrastructure/Jobs/SqlJobCommands.cs and InMemoryJobCommands.cs if their legacy branches become dead; tests/WorkerDock.IntegrationTests/TemplateCatalogSqlTests.cs, TemplateCatalogEndpointTests.cs, SqlProfileSqlTests.cs, SqlProfileEndpointTests.cs.

**Interfaces:** Each host registers exactly the SQL and HTTP 1.1 descriptors. SQL profile diagnostics require and create workerdock.sql-server@1.1.0. GET /api/templates and direct GET of the removed built-in version expose no 1.0.0.

- [ ] **Step 1: Write failing tests.** Assert Memory catalog contains only the two built-in 1.1.0 entries, SQL catalog after migration exposes no old entry, GET /api/templates/{id}/1.0.0 is 404 for both built-ins, and SQL profile diagnostic saves a 1.1.0 revision. Check worker and executor aggregate digest is computed from those same two descriptors.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj --filter 'FullyQualifiedName~TemplateCatalogSqlTests|FullyQualifiedName~TemplateCatalogEndpointTests|FullyQualifiedName~SqlProfileSqlTests|FullyQualifiedName~SqlProfileEndpointTests'`; expect the new assertions to fail.
- [ ] **Step 3: Update composition and diagnostics.** Remove old descriptors from each host and authoring registration, and remove the now-obsolete built-in bootstrap-placeholder promotion branch in TemplateMetadata. Change both hardcoded diagnostic references to 1.1.0. Remove only obsolete built-in 1.0 guards; keep custom-version handling. Adjust catalog tests that intentionally exercise a generic custom 1.0 version rather than deleting that coverage.
- [ ] **Step 4: Run green.** Run Task 4 command and `dotnet build WorkerDock.slnx --no-restore`; expect pass.
- [ ] **Step 5: Commit.** Stage only Task 4 files, run `git diff --cached --check`, commit `feat: register only built-in template v1.1`.

### Task 5: Documentation and release verification

**Files:** Modify frontend/apps/tenant/src/help/content/en/job-inputs.md, pt-BR/job-inputs.md, en/templates.md, pt-BR/templates.md; docs/how-to/upgrade-and-restore.md, docs/reference/http-template.md, docs/reference/sql-template.md, frontend/e2e/templates.spec.ts, frontend/apps/tenant/src/help/content-integrations.test.ts, and tests/WorkerDock.IntegrationTests/MigrationInputTests.cs where they present supported versions or assert upgrade guidance.

**Interfaces:** Documentation states 1.1.0 is the sole supported built-in version and describes the 0010 preflight, stopped services, backup, and failure recovery. The Memory-mode 503 explanation remains unchanged.

- [ ] **Step 1: Write failing content assertions.** Update help content tests to require 1.1.0-only wording and no instruction to select 1.0.0; assert upgrade guide names 0010 and says migration refuses databases with executions tied to the old version.
- [ ] **Step 2: Run red.** `npm.cmd --prefix frontend test -- --run apps/tenant/src/help/content-integrations.test.ts`; expect the new wording checks to fail; also run the new MigrationInputTests upgrade-guide assertion red.
- [ ] **Step 3: Update guides.** Remove obsolete user guidance for choosing built-in 1.0.0 and document the migration prechecks and restore path. Update Playwright and remaining built-in test fixtures to 1.1.0; retain generic custom-template version coverage and historical migration literals. Retain historical notes only where they explain earlier releases or migration history.
- [ ] **Step 4: Run green and final gates.** Run the Task 5 frontend command and the MigrationInputTests filter, `npm.cmd --prefix frontend run build`, `dotnet build WorkerDock.slnx --no-restore`, and `dotnet test tests/WorkerDock.IntegrationTests/WorkerDock.IntegrationTests.csproj`. Record SQL-backed tests as blocked if WORKERDOCK_TEST_SQL is unavailable; do not report them as passing.
- [ ] **Step 5: Commit.** Stage only Task 5 files, run `git diff --cached --check`, commit `docs: explain built-in template v1.1 upgrade`.
