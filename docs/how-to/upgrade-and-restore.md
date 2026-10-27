# Upgrade and restore

The tenant schema is installed only as a fresh database using `0001_initial`. No tenant databases are deployed and there is no upgrade converter. The application does not create or upgrade SQL objects at runtime. The Control Center stores persistent file state and does not migrate a database.

Fresh installations seed the SQL Server and HTTP built-in templates at version 1.1.0. Custom templates retain the versions installed by the deployment.

## Before an upgrade

1. Read the release notes and confirm the target application version. Apply `0001_initial` only to a new, empty tenant database.
2. For a tenant, use your backup process to back up the target SQL Server database and verify the backup before proceeding. For a Control Center, back up its protected state directory, configuration, keys, and application files with your chosen process.
3. Review running executions in the dashboard. Disable new work and wait for finite executions to finish. Infinite-timeout jobs can continue indefinitely; an operator must decide whether to stop them. Stop the application processes using the process manager selected for the deployment; external side effects may have happened even if WorkerDock cannot confirm completion.
4. Compare the deployed application files and protected configuration with the release record, and confirm the stable tenant or center instance ID.

## Prepare and deploy a release

Choose the deployment/build tooling for your environment. WorkerDock leaves build and deployment tooling to the operator. For example, a .NET service can be published with `dotnet publish`; build the frontend with the documented frontend build command and assemble reviewed binaries, configuration examples, SQL scripts, and permissions using your release process. Keep secrets outside artifacts.

For a new tenant only, apply `0001_initial` using `scripts/Invoke-TenantMigration.ps1`, with the reviewed Web assembly directory, separate migration credential, and `-RuntimePrincipal` naming a database user that must already exist in the target database. Use `-InitialSetup` to generate the tenant GUID, save the successful output, and copy it into `Tenant.InstanceId`. For an initialized tenant, pass the same GUID with `-InstanceId`. Preserve command output and checksum as release evidence. The script validates and grants the existing user; it does not create database users, roles, or logins, and does not build, copy, install, or start application files.

Prepare runtime configuration from `examples/config/tenant.example.json`, `worker.example.json`, and, when used, `center.example.json`. Protect config, vault references, TLS material, Data Protection keys, and archive paths under your chosen deployment system. Tenant services use one database and the existing restricted runtime SQL user; the deployment migration identity is separate. The migration validates and grants that user directly.

The migration script and `database/tenant/current-state.sql` both prepare a new, empty database. Neither updates existing tenant databases. Do not use either path for an upgrade.

After deploying a compatible application release, start the web host and one runtime service. Check `/health/ready`, the runtime heartbeat, schedules, recent executions, and logs before opening access. For the Control Center, retain its protected file state directory and stable instance ID; it has no central SQL database. Control Center configuration updates modify protected files, and the center loads persisted state on restart. Do not replace or discard its state directory during a release change.

The runtime and Executor require Windows because process containment and IPC use Windows APIs. IIS and Windows Services are optional hosting choices; configure TLS, service identities, ACLs, bindings, and process restarts with the organization's selected tools.

No built-in template conversion or tenant schema upgrade is supplied. For any future tenant data conversion, prepare a separately reviewed migration and validated backup/restore procedure before deployment.

Deploy the Web, runtime, and Executor binaries from one compatible release. If the release must be rolled back, restore a verified database backup and the matching application release; do not run a down migration against populated data. Verify `/health/ready`, runtime heartbeat, upcoming schedules, and recent execution results before opening access.

The baseline rollback guard refuses to remove populated tenant state. Do not bypass it to force a rollback. Restore the verified database backup and matching application release using the restore procedure below. A database restore replaces later database changes.

An interrupted operator deployment may leave services stopped or only some components updated. Do not restart blindly. Compare the observed files and service versions with the release record and verified backups. Reconcile manually or restore the database and previous compatible release. Use a new backup destination for each attempt.

## Restore

Application rollback is supported only when the previous application version is compatible with the current database migration level. Use release compatibility evidence and migration history to decide. Never run a down migration as a substitute for a verified restore.

For an incompatible tenant schema or damaged upgrade, stop the application and restore the verified SQL Server backup to the same isolated database identity, then point the previous compatible release at it. Preserve the tenant instance GUID and Data Protection keys. Confirm the target database identity and restore timestamp before starting any service; a restore replaces all later database changes.

For a center, restore the operator-verified copy of its protected state directory, configuration, and compatible application release using the organization's backup tooling. Keep the same `Central.InstanceId`, state directory and Data Protection keys, then start the center and review readiness. The health snapshot is advisory and may become stale; persisted file state is authoritative. Do not restore center files over tenant files or a different center identity.

WorkerDock delivery is at least once. Restoring a backup or losing a process can cause already-started external work to be retried. Use idempotent job handlers and downstream idempotency keys where possible, then review execution history and external systems after recovery.
