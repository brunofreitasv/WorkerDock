# Deploy WorkerDock

This guide describes the operator-managed deployment sequence for one tenant and, optionally, a separate Control Center. Windows Server, IIS, and Windows services are optional examples; another host and service manager can implement the same requirements. A tenant has a Web process, Scheduler process, one or more Worker processes, and one tenant database. The Control Center is separate, stores its state in protected files, and does not host tenant work or provision tenants.

Production readiness is still under validation. The repository does not include
a production `ISecretVault` integration for the Control Center; its production
host fails startup until a trusted adapter is registered. The tenant executor
and scheduler currently call the development vault registration, which returns
an unavailable provider unless the development-only environment mapping is
enabled. Install and review your tenant vault adapter before relying on
secret-backed jobs or alert sinks. These integration steps require code and deployment configuration; the repository does not include an enterprise vault.
Complete the [release checklist](../validation/release-checklist.md) on disposable
infrastructure before using this procedure for a live workload.

## Requirements and identities

For the optional Windows/IIS deployment example, use Windows Server, PowerShell 7, the .NET 10 Hosting Bundle, IIS and a trusted HTTPS certificate. Configure TLS and service identities with your selected tooling. Tenant databases require SQL Server 2022 or later with compatibility level 160; create a dedicated database for each tenant. A fresh Control Center deployment has no SQL Server, central database, schema, SQL principal, or connection string.

Use a migration credential separate from the restricted runtime SQL identity. Protect migration credentials and database backups through your organization's approved processes. Runtime IIS and service identities use a restricted SQL login and password reference, and must not have DDL rights. Keep protected config files and Data Protection keys outside release packages. Set ACLs on protected configuration and key directories for the configured identities and administrators. SQL connections require encryption and valid server certificate verification; install the issuing CA/server certificate in each host trust store. Restart affected processes after password rotation. Preserve tenant and central instance GUIDs across restarts and upgrades.

## Build and prepare SQL

Choose and review build and deployment tooling for your environment. WorkerDock leaves build and deployment tooling to the operator. For example, publish .NET services with `dotnet publish` and build the frontend with `npm.cmd --prefix frontend run build`, then assemble only reviewed release files. These are examples; package contents, signing, transfer, installation, configuration protection, and service registration are operator-selected.

Create one dedicated tenant database per tenant at compatibility level 160. Use either ordered migrations through `scripts/Invoke-TenantMigration.ps1` with an explicit target, a reviewed Web assembly directory, and an operator-supplied migration credential, or `database/tenant/current-state.sql` for a fresh empty database only. The snapshot cannot be used to upgrade an existing database. These paths are design alternatives; live SQL parity has not been verified. Use the separate deployment identity for migration and `database/tenant/permissions.sql` to grant an existing runtime principal membership in `WorkerDockRuntime`.

## Deploy a tenant

Prepare protected runtime files using `examples/config/tenant.example.json` and `worker.example.json`. Set the same stable `Tenant.InstanceId` and SQL database in Web, Scheduler, and each Worker configuration. Configure the restricted runtime identity, password reference, production vault adapter, TLS trust, Data Protection key directory, worker installation GUID and catalog digest. Keep secrets out of files and artifacts.

Before applying any migration, take and verify the operator-managed SQL backup. Block admission and dispatch, review and drain active executions, and stop Web, Scheduler, Worker and Executor processes. Apply the explicitly selected migration. Record migration result and checksums with the release evidence.

Assemble and deploy the chosen application binaries and frontend using organization-approved tooling. Preserve per-tenant configuration and stable instance IDs. Runtime configuration changes take effect after restarting affected processes. Start the processes and web host manually, then verify `/health/ready`, worker heartbeats, schedules, recent executions and logs before reopening admission. Use the release compatibility window and backup to guide recovery; see [upgrade and restore](upgrade-and-restore.md).

Windows Server and IIS are optional deployment examples. If used, configure bindings, TLS, service identities and ACLs through the selected tooling; no repository lifecycle scripts perform these steps.

## Deploy the Control Center

Prepare protected configuration from `examples/config/center.example.json`. Set a stable `Central.InstanceId`, absolute persistent `Central.Storage.DataDirectory` outside release and web directories, allowed health hosts, protected key path, and the production vault adapter. The Control Center stores tenant registrations and health state in files and does not use SQL. Back up its state and configuration through operator-selected tooling before a release change. Preserve the state directory and instance ID; configuration updates are persisted in files and take effect after process restart. Verify center `/health/ready` and registered tenant probes after start.

See [tenant configuration](configure-tenants.md), [SQL setup](setup-sql-server.md), and [upgrade and restore](upgrade-and-restore.md). Complete the [release checklist](../validation/release-checklist.md) on disposable infrastructure before live use.
