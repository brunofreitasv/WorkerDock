# Manual deployment and tenant database state

## Intent and boundaries

WorkerDock supplies application code, configuration examples, and tenant SQL assets. Developers build and deploy the web, scheduler, worker, executor, and optional Control Center with their own deployment tools and directory layout. Repository scripts must not be a required install, upgrade, uninstall, packaging, or service-management path. Database changes are explicit operator actions; application startup never applies them.

This design replaces the current opinionated deployment scripts and standalone .NET database migrator. It retains two ways to provision a tenant database: a manually invoked PowerShell migration script and a plain SQL desired-state snapshot for fresh databases. The snapshot path is not an upgrade path for populated databases.

## Repository components

- Remove `deploy/Install-Tenant.ps1`, `Install-Worker.ps1`, `Install-Center.ps1`, `Upgrade-Instance.ps1`, `Uninstall-Instance.ps1`, `Build-Packages.ps1`, and `WorkerDock.Deployment.psm1`, along with tests that exist only for those commands and the custom ZIP manifests they generate.
- Remove the `WorkerDock.DatabaseMigrator` project, executable, solution entry, package content, and CLI-specific tests. Move its reusable transactional migration runner and the `0010_remove_builtin_v1` template preflight into `WorkerDock.Infrastructure`. Existing integration tests call that library directly after their project references are updated.
- Retain `database/tenant/manifest.json` and its versioned `.up.sql` and `.down.sql` files as the canonical incremental migration source. Do not rewrite historical scripts or their recorded checksums. Retain SQL permission assets and sample configuration as source examples, relocating them out of `deploy/` if needed to avoid implying an installer-owned layout.
- Add `scripts/Invoke-TenantMigration.ps1` as an optional, manually run entry point. It loads built WorkerDock Infrastructure and SQL client assemblies in a PowerShell host compatible with WorkerDock's .NET runtime, then calls the shared migration logic. It does not install binaries, manage services, package releases, or create backups.
- Add one checked-in plain SQL snapshot template under `database/tenant/` that defines the current desired schema, required static data, tenant identity initialization, and a migration-history baseline. It is a consolidated final state, not a concatenation of incremental `.up.sql` files. Its target migration ID and checksum information must agree with the canonical manifest.

## Manual migration path

The operator supplies SQL host, port, database, a stable tenant instance GUID, a target migration ID, a `PSCredential` obtained through the operator's own vault integration, and paths to the SQL assets and built WorkerDock assemblies. The script builds a SQL-authenticated connection with encryption and certificate validation. It must not print or persist the password or a connection string. Runtime tenant hosts continue to resolve their own logical password references through `ISecretVault`; the manual script's credential contract does not change runtime configuration.

For an existing database, the operator first makes and verifies a backup using their own tools and stops or drains the relevant application processes according to their environment. The script does not perform or verify that backup. Invocation is always explicit and targets a named migration. Applying a target validates the manifest and both file checksums, SQL Server prerequisites, tenant identity, and contiguous recorded history; it serializes changes with the schema application lock and applies each unapplied migration in a transaction. The `0010_remove_builtin_v1` data preflight remains intact. Guarded single-step rollback remains available, with restore from the operator's backup recommended when a down script cannot safely reverse populated data.

The script fails before mutation for missing or malformed inputs, unknown targets, checksum drift, instance or history mismatch, unsupported SQL Server settings, or failed preflight. A failed migration transaction rolls back and releases its lock. Errors use stable, nonsecret codes; provider and SQL credentials are never included. There is no automatic retry or application release switch.

## Fresh-database snapshot path

The snapshot is for a newly provisioned, empty tenant database. The developer's chosen database deployment tool renders tenant-specific values, including the stable instance GUID, and deploys the rendered snapshot as its only database action. The rendered artifact must include the final schema, roles and permissions needed by WorkerDock, required static rows, the tenant identity row, and manifest-aligned history rows. It must fail if required placeholders remain unsubstituted. SQL login creation and identity policy remain with the operator; principal binding in the rendered database state must match the runtime identity they choose.

The history baseline records the state represented by the snapshot so a later explicit incremental migration can start from that target. Its history rows assert that the corresponding migration levels are satisfied by equivalent state; they do not assert that each historical `.up.sql` file ran. Documentation explains these baseline semantics. A populated database must use the incremental path or a separately designed data-conversion process. Applying the snapshot to an existing tenant is unsupported because schema state alone cannot safely transform existing rows such as legacy job revisions.

Maintainers update the snapshot whenever a migration changes the current desired state. A disposable SQL integration check compares databases provisioned from the full migration chain and from the snapshot, including schema objects, required seed rows, tenant identity, and history continuity. A release cannot claim snapshot parity from static file checks alone.

## Documentation

Rewrite the getting-started, SQL setup, production deployment, upgrade/restore, deployment model, configuration, and database reference guides to describe required processes and settings without prescribing a package format, install root, IIS layout, deployment pipeline, or repository installer. Show `dotnet publish` and frontend build commands only as examples. Explain that operators place binaries and protected configuration with their own tooling, provision one database per tenant, choose one database path, start components in a safe order, and verify readiness. Windows/IIS guidance may remain as an explicitly optional example where supported by the runtime.

Document the manual upgrade order: review compatibility and active work, make and verify backups through the operator's process, stop or drain affected processes, run the explicit migration command when using incremental SQL, deploy matching application binaries through the chosen tool, restart, and check readiness. Document recovery using the operator's database and application backups. Remove instructions that require `Upgrade-Instance.ps1`, custom WorkerDock ZIP packages, or the migrator executable. Preserve the runtime tenant SQL vault contract and restart-on-password-rotation guidance.

## Verification and acceptance

- No application startup path applies SQL migrations, and no deployment lifecycle script or DatabaseMigrator executable remains in the supported flow.
- The manual PowerShell script accepts caller-supplied credentials and metadata, invokes the shared checks, and reports failures without exposing secrets.
- Incremental migrations retain checksum, history, instance identity, SQL prerequisite, locking, transaction, and legacy-template preflight behavior.
- A fresh database deployed from the rendered snapshot has the same effective WorkerDock schema and required initialization state as one built from the full migration chain. The rendered snapshot contains no unresolved tenant-specific placeholders and is rejected if required values were not supplied.
- Tests use the relocated migration library, cover script input and error behavior, and compare both database paths on a disposable SQL Server. Without that fixture, SQL parity and live migration results are reported as blocked, not passed.
- Current operator docs describe manual, tool-neutral deployment and both database paths; optional examples are clearly labeled and do not impose a deployment tool.

## Out of scope

This change does not provide a deployment pipeline, installer, release switcher, production vault adapter, backup tool, or a snapshot upgrade path for populated databases. It does not change the tenant runtime vault-based SQL connection design.
