# Tenant SQL Vault Credentials Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build tenant SQL connections from nonsecret settings and vault resolved passwords in every runtime and deployment SQL path.

**Architecture:** One infrastructure resolver validates host, port, database, user, and logical reference, then builds an internal SqlClient connection string. Web, worker, scheduler, and migrator resolve once at startup; the migrator also owns upgrade backup and history checks. Installers and docs accept only the new settings.

**Tech Stack:** .NET 10, C#, Microsoft.Data.SqlClient, xUnit, PowerShell 7, SQL Server 2022.

**Spec:** docs/superpowers/specs/2026-10-22-tenant-sql-vault-credentials-design.md

## Global Constraints

- Config keys are Tenant:Sql:{Host,Port,Database,User,PasswordReference} and Migrator:Sql with the same five leaves.
- Host is bare DNS/IP; port is 1–65535; database and user are 1–128 characters; reference is 1–128 ASCII letters, digits, underscores, dashes, or dots.
- SQL authentication uses mandatory encryption, validates the server certificate, and sets PersistSecurityInfo=false.
- Tenant:ConnectionString is rejected by runtime hosts and installers, including an empty value. WORKERDOCK_SQL is rejected by migrator and upgrade.
- EnvironmentSecretVault is usable for tenant SQL only in Development with WORKERDOCK_DEVELOPMENT_VAULT=1. Production needs a registered ISecretVault.
- Resolve once per process; password rotation requires restart. Never log or persist the password or constructed string. Safe codes are SqlConfigurationInvalid, LegacyConnectionStringUnsupported, SecretUnavailable, and PersistenceUnavailable.
- Preserve the current dirty worktree. Inspect each target diff, stage only new hunks with git add -p, and verify git diff --cached before each commit.

## Review Focus

1. Empty legacy Tenant:ConnectionString still means legacy configuration and must fail (Task 2 test).
2. Host text containing a port, SQL instance, or connection string delimiter must fail before secret lookup (Task 1 test).
3. Passwords containing semicolons or quotes must survive SqlConnectionStringBuilder escaping without appearing in errors (Task 1 test).
4. Vault exceptions containing sensitive text must become a safe SecretUnavailable failure (Task 1 test).
5. Existing or non-UNC upgrade backup paths must fail before SQL access or release staging (Task 4 test).

## File map

- Create src/WorkerDock.Infrastructure/Persistence/TenantSqlConfiguration.cs for nonsecret parsing and legacy checks.
- Create src/WorkerDock.Infrastructure/Persistence/TenantSqlConnectionResolver.cs for vault lookup and SqlClient construction.
- Modify src/WorkerDock.Infrastructure/Secrets/SecretVaultRegistration.cs for Development only tenant SQL registration.
- Create src/WorkerDock.Web/TenantSqlStartup.cs and modify src/WorkerDock.Web/Program.cs, src/WorkerDock.Worker/Program.cs, and src/WorkerDock.Scheduler/Program.cs for startup wiring.
- Modify src/WorkerDock.DatabaseMigrator/Program.cs and MigrationRunner.cs; create MigrationRunner.Upgrade.cs for backup/history preparation.
- Modify deploy/WorkerDock.Deployment.psm1 and deploy/Upgrade-Instance.ps1 for protected config and migrator invocation.
- Update deploy/config examples and the SQL, deployment, upgrade, configuration, and secrets docs named in the spec.

---

### Task 1: Shared SQL credential resolver

**Files:** Create src/WorkerDock.Infrastructure/Persistence/TenantSqlConfiguration.cs and TenantSqlConnectionResolver.cs; modify src/WorkerDock.Infrastructure/Secrets/SecretVaultRegistration.cs; create tests/WorkerDock.UnitTests/TenantSqlConnectionTests.cs.

**Interfaces:** Produce `TenantSqlSettings(string Host, int Port, string Database, string User, SecretReference PasswordReference)`; `TenantSqlConfiguration.Read(IConfiguration config, string section): TenantSqlSettings`; `TenantSqlConfiguration.RejectLegacy(IConfiguration config, string key): void`; `TenantSqlConnectionResolver.ResolveAsync(TenantSqlSettings settings, ISecretVault vault, CancellationToken ct): ValueTask<string>`; `SecretVaultRegistration.CreateTenantSql(bool isDevelopment): ISecretVault`.

- [ ] **Step 1: Write failing tests.** In TenantSqlConnectionTests, assert Read rejects missing leaves, port 0/65536, host `db,1444`, `db\\INSTANCE`, and `db;Password=x` with SqlConfigurationInvalid before vault lookup. Assert RejectLegacy rejects an empty key with LegacyConnectionStringUnsupported. Resolve a synthetic password `p;ass"word`, parse the result with SqlConnectionStringBuilder, and assert DataSource=`tcp:db.example,1433`, InitialCatalog, UserID, Password, Encrypt=Mandatory, TrustServerCertificate=false, and PersistSecurityInfo=false. Make a throwing vault include `SENTINEL` in its exception; assert SecretUnavailable and no sentinel or inner exception.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.UnitTests -c Release --filter FullyQualifiedName~TenantSqlConnectionTests`; expect missing types/tests to fail.
- [ ] **Step 3: Implement the listed interfaces.** Validate before lookup; use SqlConnectionStringBuilder for every field; dispose SecretValue; map provider errors to SecretUnavailable without provider text. CreateTenantSql returns the existing development provider only when isDevelopment is true, otherwise an unavailable provider. Test that the false case stays unavailable even when the development flag is set.
- [ ] **Step 4: Run green.** Run the Task 1 filter; expect pass.
- [ ] **Step 5: Commit.** Stage only Task 1 hunks, inspect the staged diff, and commit `feat: resolve tenant SQL credentials through vault`.

### Task 2: Tenant web startup

**Files:** Create src/WorkerDock.Web/TenantSqlStartup.cs; modify src/WorkerDock.Web/Program.cs; modify tests/WorkerDock.IntegrationTests/WebHostTests.cs, AccessPolicyTests.cs, MonitoringAccessTests.cs, and ExecutionControlsEndpointTests.cs; create tests/WorkerDock.IntegrationTests/TenantSqlWebStartupTests.cs.

**Interfaces:** Consume Task 1 Read, RejectLegacy, ResolveAsync, and CreateTenantSql. Produce `TenantSqlStartup(IConfiguration config, IHostEnvironment environment, ISecretVault vault)`, `InitializeAsync(CancellationToken ct): ValueTask`, and `Options: TenantDatabaseOptions`; SQL factories read Options after initialization.

- [ ] **Step 1: Write failing tests.** TenantSqlWebStartupTests asserts SQL mode with missing database fields fails before serving, and memory mode with `Tenant:ConnectionString=""` fails with LegacyConnectionStringUnsupported. Update existing memory fixtures to omit the legacy key. Replace the old “Production SQL missing config returns readiness 503” assertion with startup failure SqlConfigurationInvalid; keep a separate memory readiness check.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter "FullyQualifiedName~TenantSqlWebStartupTests|FullyQualifiedName~WebHostTests"`; expect startup assertions to fail.
- [ ] **Step 3: Wire web startup.** Register the Development-only fallback vault in DI, allow a real provider registration to replace it, build the app, then await TenantSqlStartup.InitializeAsync before RunAsync. InitializeAsync rejects legacy config in every mode and resolves one TenantDatabaseOptions in SQL mode; SQL factories use TenantSqlStartup.Options instead of Tenant:ConnectionString. Keep Development Memory behavior and existing health/authorization behavior.
- [ ] **Step 4: Run green.** Run the Task 2 filter plus AccessPolicyTests, MonitoringAccessTests, and ExecutionControlsEndpointTests; expect pass.
- [ ] **Step 5: Commit.** Stage only Task 2 hunks, inspect the staged diff, and commit `feat: use vault credentials for tenant web SQL`.

### Task 3: Scheduler and worker startup

**Files:** Modify src/WorkerDock.Scheduler/Program.cs and src/WorkerDock.Worker/Program.cs; modify tests/WorkerDock.ProcessTests/WorkerDock.ProcessTests.csproj; create tests/WorkerDock.ProcessTests/TenantSqlHostStartupTests.cs.

**Interfaces:** Consume Task 1 interfaces; construct existing TenantDatabaseOptions and QuartzConnections from one resolved string per process.

- [ ] **Step 1: Write failing process tests.** Launch scheduler and worker binaries with valid nonsecret fields, Development enabled, and an unmapped password reference. Assert both exit without opening SQL, report SecretUnavailable, and do not print a synthetic password or environment variable name. Add project build references for both binaries to the process test project and launch their Release net10.0-windows DLL paths.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.ProcessTests -c Release --filter FullyQualifiedName~TenantSqlHostStartupTests`; expect missing startup behavior.
- [ ] **Step 3: Wire both hosts.** Register ISecretVault in each host composition root with a Development-only fallback, validate existing instance/worker settings, reject legacy config, resolve once before SQL/Quartz construction, and reuse the string only inside the process. Register a separate scheduler alert vault as already required.
- [ ] **Step 4: Run green.** Run the Task 3 filter; expect pass.
- [ ] **Step 5: Commit.** Stage only Task 3 hunks, inspect the staged diff, and commit `feat: use vault credentials in scheduler and worker`.

### Task 4: Migrator and upgrade preparation

**Files:** Modify src/WorkerDock.DatabaseMigrator/Program.cs and MigrationRunner.cs; create src/WorkerDock.DatabaseMigrator/MigrationRunner.Upgrade.cs; modify tests/WorkerDock.IntegrationTests/MigrationInputTests.cs and MigrationTests.cs.

**Interfaces:** Consume Task 1 Read and ResolveAsync. Add `MigrationRunner.PrepareUpgradeAsync(string databaseKind, string targetMigration, string backupPath, CancellationToken ct): ValueTask`. CLI: `apply|rollback tenant <migration>` and `prepare-upgrade tenant <target-migration> <backup-path>`; read Migrator:Sql from WORKERDOCK_MIGRATOR_CONFIG_PATH JSON or .NET environment settings, keep WORKERDOCK_INSTANCE_ID.

- [ ] **Step 1: Write failing tests.** Reject WORKERDOCK_SQL even if empty; reject missing Migrator:Sql and unavailable reference without printing secrets. PrepareUpgradeAsync rejects wrong database kind, unknown target, existing backup, non-UNC path, and non-.bak extension before connecting. Where WORKERDOCK_TEST_SQL_BACKUP_UNC is configured, verify a disposable tenant backup is created and RESTORE VERIFYONLY succeeds.
- [ ] **Step 2: Run red.** `dotnet test tests/WorkerDock.IntegrationTests -c Release --filter "FullyQualifiedName~MigrationInputTests|FullyQualifiedName~MigrationTests"`; expect new cases to fail.
- [ ] **Step 3: Implement migrator flow.** Make MigrationRunner partial, put history-prefix/checksum verification and copy-only BACKUP plus RESTORE VERIFYONLY in MigrationRunner.Upgrade.cs, and update Program CLI/config parsing. Register the Development-only fallback vault in the migrator composition root, require a real provider in Production, and keep existing apply/rollback checks and safe exit codes.
- [ ] **Step 4: Run green.** Run the Task 4 filter; expect pass, or the existing SQL fixture's documented SQL_TEST_BLOCKED outcome when no disposable SQL is configured.
- [ ] **Step 5: Commit.** Stage only Task 4 hunks, inspect the staged diff, and commit `feat: migrate and back up tenant SQL through vault`.

### Task 5: Deployment validation and upgrade script

**Files:** Modify deploy/WorkerDock.Deployment.psm1 and deploy/Upgrade-Instance.ps1; create tests/deploy/tenant-sql-vault.Tests.ps1.

**Interfaces:** Consume Task 4 CLI. Add and export PowerShell `Assert-TenantSqlConfig($Config, $Section)` in WorkerDock.Deployment.psm1 for tenant and worker validation; add optional `-MigratorConfigPath` to Upgrade-Instance.ps1, required only for tenant upgrades.

- [ ] **Step 1: Write failing PowerShell tests.** Assert valid five-field tenant/worker config passes; missing port, partial fields, and present legacy ConnectionString fail. Assert tenant upgrade requires MigratorConfigPath and center upgrade does not. Parse upgrade script to assert it invokes prepare-upgrade before Install-VerifiedRelease and apply afterward, and contains no SQL client connection creation or WORKERDOCK_SQL assignment. Keep center upgrade tests unchanged.
- [ ] **Step 2: Run red.** `pwsh -NoProfile -File tests/deploy/tenant-sql-vault.Tests.ps1`; expect failures.
- [ ] **Step 3: Update scripts.** Validate persisted config and migrator path; pass only path, instance ID, target, and backup path to migrator. Remove direct PowerShell SQL backup/history and full string environment handoff. Preserve tenant stop checks, backup verification, journaling, and center branch.
- [ ] **Step 4: Run green.** Run the Task 5 script and `pwsh -NoProfile -File tests/deploy/center-storage.Tests.ps1`; expect pass.
- [ ] **Step 5: Commit.** Stage only Task 5 hunks, inspect the staged diff, and commit `feat: deploy tenant SQL with vault references`.

### Task 6: Examples and operator instructions

**Files:** Modify deploy/config/tenant.example.json, deploy/config/worker.example.json, docs/tutorials/run-with-sql-server.md, docs/how-to/setup-sql-server.md, docs/how-to/configure-tenants.md, docs/how-to/deploy-production.md, docs/how-to/upgrade-and-restore.md, docs/reference/configuration.md, and docs/reference/secrets.md; create deploy/config/migrator.example.json; modify tests/DeploymentSmoke.ps1.

**Interfaces:** Document exact Task 1 and Task 4 keys and the Task 5 upgrade parameter.

- [ ] **Step 1: Write failing smoke assertions.** DeploymentSmoke verifies all tenant/worker/migrator examples have the five keys, no ConnectionString, and that the migrator example uses a distinct password reference. Existing example validation remains.
- [ ] **Step 2: Run red.** `pwsh -NoProfile -File tests/DeploymentSmoke.ps1`; expect example checks to fail.
- [ ] **Step 3: Update examples and docs.** Replace full-string setup with host, port, database, user, logical reference, Development vault mapping, TLS certificate requirements, restart on rotation, separate migrator rights, and -MigratorConfigPath. Preserve unrelated tutorial and upgrade edits already in the worktree.
- [ ] **Step 4: Run green.** Run DeploymentSmoke and the focused .NET/PowerShell checks above; expect pass. Inspect docs for active instructions still setting Tenant__ConnectionString or WORKERDOCK_SQL.
- [ ] **Step 5: Commit.** Stage only Task 6 hunks, inspect the staged diff, and commit `docs: configure tenant SQL through vault references`.

## Final verification

Run `dotnet build WorkerDock.slnx -c Release --no-restore`, the three focused test projects and two deployment scripts above. Inspect `git diff --check`, staged/unstaged status, and the SQL tutorial's commands. If disposable SQL and backup UNC are configured, run the migrator backup integration case; otherwise report that live SQL proof was not run.
