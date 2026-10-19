# Deploy WorkerDock on Windows Server

This procedure deploys one tenant and, optionally, a separate Control Center. A tenant consists of an IIS web application, a scheduler Windows service and one or more generic worker Windows services. The Control Center is a separate IIS application with its own database. It does not host tenant work or provision tenants.

Production readiness is still under validation. The repository does not include
a production `ISecretVault` integration for the Control Center; its production
host fails startup until a trusted adapter is registered. The tenant executor
and scheduler currently call the development vault registration, which returns
an unavailable provider unless the development-only environment mapping is
enabled. Install and review your tenant vault adapter before relying on
secret-backed jobs or alert sinks. These integration steps require code and
deployment configuration; the installer does not supply an enterprise vault.
Complete the [release checklist](../release-checklist.md) on disposable
infrastructure before using this procedure for a live workload.

## Requirements and identities

Use Windows Server with IIS and the WebAdministration PowerShell module, PowerShell 7, the .NET 10 Hosting Bundle and a trusted HTTPS certificate in `LocalMachine\My`. Use SQL Server 2022 or later with compatibility level 160. Create a dedicated database for every tenant and a separate central database if using the Control Center. Each application identity must have only its corresponding runtime database role: `WorkerDockRuntime` in a tenant database or `WorkerDockCentralRuntime` in the central database. Run installers from an elevated PowerShell 7 session.

Use a deployment-only SQL identity for migrations and database backup operations. Runtime IIS and service identities use integrated authentication when possible and must not have DDL rights. Keep configuration, database credentials and Data Protection keys outside release packages. Install scripts restrict config and key directories to the configured identities and administrators. Preserve tenant and central instance GUIDs across restarts and upgrades.

## Build and prepare SQL

Build signed-off release packages from a clean source checkout:

```powershell
pwsh -File .\deploy\Build-Packages.ps1 -Version 1.0.0
```

The script produces tenant and center ZIP packages under `artifacts/packages/`; each includes a hash manifest and database migrator. Set the four `-TenantSchemaMinVersion`, `-TenantSchemaMaxVersion`, `-CenterSchemaMinVersion` and `-CenterSchemaMaxVersion` options to the release's tested schema compatibility window.

Create empty databases at compatibility level 160, then apply their migration manifests using the deployment-only migrator connection. The exact migrator syntax and tenant/central targets are in [SQL setup and migrations](setup-sql-server.md). Bind the application principals to runtime roles after migration. From the repository root, for example:

```powershell
sqlcmd -S '<SQL_SERVER>' -d '<TENANT_DATABASE>' -E -b `
  -v TenantRuntimePrincipal="<TENANT_DB_USER>" CentralRuntimePrincipal="" `
  -i .\deploy\permissions.sql
```

For the central database set `TenantRuntimePrincipal=""` and `CentralRuntimePrincipal` to the central database user. The role binding script does not create principals, schema or grants; migrations create the runtime roles and grants. See [SQL setup](setup-sql-server.md) for rollback constraints and migration IDs.

## Install a tenant

Copy `deploy/config/tenant.example.json` and `worker.example.json` to protected deployment storage. Set the same `Tenant.InstanceId` GUID in both files, the runtime connection string, Entra settings (if used), absolute Data Protection key and archive directories, and the worker's unique installation GUID, installed catalog digest, ceiling and drain time. Replace every placeholder. Use `Access.RequiredManagementRole` only when management must be restricted by role; remove it to allow any authenticated user to manage. See [tenant configuration](configure-tenants.md) and [authentication](../authentication.md).

Install using the package version whose migrations are applied:

```powershell
pwsh -File .\deploy\Install-Tenant.ps1 `
  -InstanceId '<TENANT_GUID>' `
  -InstallRoot 'C:\Program Files\WorkerDock\<TENANT>' `
  -ConfigPath 'C:\WorkerDockConfig\<TENANT>\tenant.json' `
  -WorkerConfigPath 'C:\WorkerDockConfig\<TENANT>\worker.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-tenant-1.0.0.zip' `
  -SiteName 'WorkerDock-<TENANT>' `
  -HostName '<TENANT_HOSTNAME>' `
  -CertificateThumbprint '<CERTIFICATE_THUMBPRINT>'
```

The installer verifies package hashes, creates an HTTPS SNI binding on port 443, an IIS application pool, scheduler and worker services, and protected persistent config. It uses the supplied service account options or the per-service virtual accounts. Accepted service identities are the service virtual account, LocalService, NetworkService or a gMSA; ordinary password-based accounts are rejected. The scheduler runs as `Production`; each worker has a service-specific environment name. The installer does not start services or the site. Review configuration, SQL connectivity and ACLs before starting them.

The web host, scheduler and worker are separate processes. The scheduler owns schedules, dispatch/reconciliation and maintenance; worker services acquire execution work and start contained executor children. Add independent process capacity with another worker installation GUID and service name:

```powershell
pwsh -File .\deploy\Install-Worker.ps1 `
  -InstanceId '<TENANT_GUID>' `
  -InstallRoot 'C:\Program Files\WorkerDock\<TENANT>' `
  -ConfigPath 'C:\WorkerDockConfig\<TENANT>\worker-02.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-tenant-1.0.0.zip' `
  -ServiceName 'WorkerDock-<TENANT>-Worker02'
```

Its config needs the same tenant GUID and connection, but its own installation GUID. Keep its package version aligned to the active tenant release. Job creation and concurrency limits do not install services.

## Install the Control Center

The center uses its own central instance GUID, database, app registration, keys and runtime identity. Prepare `center.json` from `deploy/config/center.example.json`; set `Central.InstanceId`, runtime connection, `Central.Health.AllowedHosts`, center access settings and protected key path. Install it separately:

```powershell
pwsh -File .\deploy\Install-Center.ps1 `
  -InstanceId '<CENTER_GUID>' `
  -InstallRoot 'C:\Program Files\WorkerDock\ControlCenter' `
  -ConfigPath 'C:\WorkerDockConfig\ControlCenter\center.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-center-1.0.0.zip' `
  -SiteName 'WorkerDock-ControlCenter' `
  -HostName '<CONTROL_CENTER_HOSTNAME>' `
  -CertificateThumbprint '<CERTIFICATE_THUMBPRINT>'
```

The center health vault must be implemented for production and bound to a center-only identity; the application deliberately fails production startup without an `ISecretVault` adapter. Resolve only approved tenant health credential references. Do not grant that identity access to tenant databases or credentials. Details for tenant registration URL and credential reference are in [configure tenants](configure-tenants.md) and [Control Center](../control-center.md).

## Start and check

After checking SQL roles and app settings, start each configured Windows service and IIS site/application pool manually. Verify the web `/health/ready` endpoint, service status, event logs and worker heartbeats before directing users to the dashboard. The Control Center health check probes only each registered tenant's fixed `/health/ready` endpoint; its result is advisory. Protect all public bindings with TLS. If TLS terminates at a proxy, configure trusted forwarding explicitly.

## Upgrade and rollback

Read [upgrade and restore](../upgrades.md) before each release. Block new admissions, inspect running executions and decide how to handle infinite-timeout jobs. Stop tenant services, IIS site and application pool manually. Then run the upgrade script with the persisted config, matching package, and a new UNC `.bak` location writable by the SQL Server service account:

```powershell
pwsh -File .\deploy\Upgrade-Instance.ps1 `
  -InstallRoot 'C:\Program Files\WorkerDock\<TENANT>' `
  -ConfigPath 'C:\Program Files\WorkerDock\<TENANT>\config\tenant.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-tenant-1.1.0.zip' `
  -BackupPath '\\<SQL_BACKUP_SERVER>\WorkerDock\<TENANT>-before-1.1.0.bak'
```

For a center upgrade, pass its `center.json` and center package. The script verifies release hashes and schema compatibility, makes and verifies a SQL `COPY_ONLY` checksum backup plus application archive, applies included migrations, switches the release and leaves services and IIS stopped for review. Start them manually and inspect readiness and executions. If the new release is incompatible with the database or recovery is needed, restore the verified SQL backup and run the previous compatible release; preserve instance GUID and Data Protection keys. Do not use a down migration as a production rollback. An incomplete upgrade journal requires manual reconciliation against migration history and both backups before retrying.

\n