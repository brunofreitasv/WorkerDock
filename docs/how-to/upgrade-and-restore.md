# Upgrade and restore

Tenant releases include versioned database migrations. The application will not create or upgrade SQL objects at runtime. Review the release notes for application/database compatibility and the explicit migration target. The Control Center stores persistent file state and does not migrate a database.

The earlier dynamic template-values release added tenant migration `0009_dynamic_values` and execution IPC protocol version 2. Its nullable revision/context/snapshot columns preserve existing fixed-value jobs; there is no historical date backfill. The current built-in SQL Server and HTTP template version is 1.1.0. Migration `0010_remove_builtin_v1` upgrades every stored built-in 1.0.0 job revision and removes the old catalog and worker capability entries. Custom templates keep their own versions.

## Before an upgrade

1. Read the release notes and confirm the target application version and tenant migration ID, if the database changes.
2. For a tenant, use your backup process to back up the target SQL Server database and verify the backup before proceeding. For a Control Center, back up its protected state directory, configuration, keys, and application files with your chosen process.
3. Review running executions in the dashboard. Disable new work and wait for finite executions to finish. Infinite-timeout jobs can continue indefinitely; an operator must decide whether to stop them. Stop the application processes using the process manager selected for the deployment; external side effects may have happened even if WorkerDock cannot confirm completion.
4. Compare the deployed application files and protected configuration with the release record, and confirm the stable tenant or center instance ID.

## Prepare and deploy a release

Choose the deployment/build tooling for your environment. WorkerDock leaves build and deployment tooling to the operator. For example, a .NET service can be published with `dotnet publish`; build the frontend with the documented frontend build command and assemble reviewed binaries, configuration examples, SQL scripts, and permissions using your release process. Keep secrets outside artifacts.

Before any schema change, take and verify the operator-managed SQL Server backup. Block admission and new dispatch, review and drain active work, and stop tenant Web, Scheduler, Worker, and Executor processes. Select the intended migration explicitly and apply it using `scripts/Invoke-TenantMigration.ps1`, with the reviewed release assemblies and tenant scripts. Preserve command output and migration history as release evidence. The script does not build, copy, install, or start application files.

Prepare runtime configuration from `examples/config/tenant.example.json`, `worker.example.json`, and, when used, `center.example.json`. Protect config, vault references, TLS material, Data Protection keys, and archive paths under your chosen deployment system. Tenant services use one database and restricted runtime SQL identity; the deployment migration identity is separate. Apply `database/tenant/permissions.sql` for the existing runtime database principal after migration.

Two SQL preparation paths are documented: ordered migrations using the explicit script, or `database/tenant/current-state.sql` for an empty fresh database only. The snapshot is not an upgrade mechanism. These paths are design alternatives; live SQL parity has not been verified.

After the application release and migration are reviewed, start the tenant services and web host manually. Check `/health/ready`, worker heartbeats, schedules, recent executions, and logs before reopening admission. For the Control Center, retain its protected file state directory and stable instance ID; it has no central SQL database. Control Center configuration updates modify protected files, and the center loads persisted state on restart. Do not replace or discard its state directory during a release change.

Worker and Executor currently require Windows because their process containment and IPC use Windows APIs. IIS and Windows Services are optional hosting choices; configure TLS, service identities, ACLs, bindings, and process restarts with the organization's selected tools. The same backup, explicit migration, stop/start, readiness, and recovery sequence applies.

For a tenant upgrade that removes built-in template 1.0.0, verify the database and release backups, block admission and new dispatch, review and drain active work, and stop the tenant Web, scheduler, and Worker services. Allow Executor child processes to exit with the workers. Apply migration `0010_remove_builtin_v1` after the earlier migrations and confirm its checksum/history entry. The migration preflight refuses to proceed if any execution refers to a built-in 1.0.0 revision, if either trusted 1.1.0 catalog descriptor is unavailable or altered, if a stored configuration fails 1.1.0 validation, or if an unexpected foreign-key reference prevents removal. It checks all historical and archived revisions and leaves tenant data unchanged when a preflight check fails. Correct the blocker or restore the matching release and retry.

Deploy compatible Web, Scheduler, Worker, and Executor binaries together. The migration cannot be reversed because the original version assignments cannot be reconstructed. If the upgrade must be rolled back, restore the verified pre-upgrade database backup and the matching application release; do not run a down migration to recreate the old catalog. After reviewing the journal, backups, and migration result, start the processes manually, verify `/health/ready`, worker heartbeats, upcoming schedules, and recent execution results, then resume admission. Keep the previous release, configuration, and backups until the rollback window ends.

Once dynamic plans, execution contexts, or value snapshots have been stored, migration `0009_dynamic_values` refuses rollback while that state is populated. Do not remove those fields or downgrade binaries to force a rollback. Drain/retain the compatible release or restore the verified pre-upgrade database backup and matching application release using the restore procedure below. A database restore replaces later database changes.

An interrupted upgrade may leave a journal and stopped services. An interrupted operator deployment may leave services stopped or only some components updated. Do not restart blindly. Compare the observed files and service versions with SQL migration history, the release record, and verified backups. Reconcile manually or restore the database and previous compatible release. Use a new backup destination for each attempt.

## Restore

Application rollback is supported only when the previous application version is compatible with the current database migration level. Use release compatibility evidence and migration history to decide. Never run a down migration as a substitute for a verified restore.

For an incompatible tenant schema or damaged upgrade, stop the application and restore the verified SQL Server backup to the same isolated database identity, then point the previous compatible release at it. Preserve the tenant instance GUID and Data Protection keys. Confirm the target database identity and restore timestamp before starting any service; a restore replaces all later database changes.

For a center, restore the operator-verified copy of its protected state directory, configuration, and compatible application release using the organization's backup tooling. Keep the same `Central.InstanceId`, state directory and Data Protection keys, then start the center and review readiness. The health snapshot is advisory and may become stale; persisted file state is authoritative. Do not restore center files over tenant files or a different center identity.

WorkerDock delivery is at least once. Restoring a backup or losing a process can cause already-started external work to be retried. Use idempotent job handlers and downstream idempotency keys where possible, then review execution history and external systems after recovery.
