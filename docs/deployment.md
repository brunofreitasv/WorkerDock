# Windows deployment

Use the [production deployment guide](how-to/deploy-production.md) for the
current sequence, [tenant configuration](how-to/configure-tenants.md) for
settings and Control Center registration, and
[SQL setup](how-to/setup-sql-server.md) for runtime and test-only objects.
This page retains additional installer behavior and operational detail.

WorkerDock runs as separate tenant applications. A tenant installation contains an IIS application, a Quartz scheduler Windows service, and one or more generic WorkerDock worker Windows services. Adding a job or changing its template concurrency does not install or remove a Windows service. Install another worker service only when you want another process or host-capacity boundary.

The Control Center is installed separately. It stores tenant URLs and health credential references in its own central database; it does not provision tenant databases or copy tenant job data. Tenant dashboard access follows the tenant's Entra authentication policy. The Control Center role requirement protects only management actions.

## Requirements

- Windows Server with IIS and the IIS WebAdministration module.
- PowerShell 7, .NET 10 Hosting Bundle, and a trusted HTTPS certificate installed in `LocalMachine\My`.
- SQL Server 2022 or later, with a separate database for each tenant and another for the Control Center.
- Runtime Windows identities mapped to the database-specific `WorkerDockRuntime` or `WorkerDockCentralRuntime` role after the matching migrations have run.
- A trusted WorkerDock tenant or center package produced from a clean source checkout.

The scripts require an elevated PowerShell session for IIS and Windows service configuration. Choose absolute paths for package, config, backup, and install roots. Do not put database, Entra, or vault credentials in package files or source control. Use integrated SQL authentication where available and restrict deployment config and Data Protection key directories to their application identities.

## Build packages

From the repository root, run:

```powershell
pwsh -File .\deploy\Build-Packages.ps1 -Version 1.0.0
```

This restores the locked frontend workspace, builds the Web and Control Center assets into isolated staging folders, publishes the tenant hosts, executor, and database migrator, writes SHA-256 entries to each package manifest, and creates tenant and center ZIP files under `artifacts/packages/`. Set `-TenantSchemaMinVersion`, `-TenantSchemaMaxVersion`, `-CenterSchemaMinVersion`, and `-CenterSchemaMaxVersion` to the release's tested inclusive compatibility windows. Do not widen a window without upgrade evidence.

The example files in `deploy/config/` use the same section names as the .NET hosts. Copy them to protected deployment storage, replace every `<PLACEHOLDER>`, and configure the worker installation GUID and the digest of the reviewed installed template catalog. The config examples contain no working credentials.

## Prepare SQL Server

Create each database and apply its versioned migrations with the database migrator before starting the application. See [database setup and migrations](database.md) for SQL Server compatibility, local setup, migration commands, and the memory test adapter. The package includes the migrator and its migration scripts.

Create or map a database principal for each application identity. After migrations, bind it to the matching runtime role in that database:

```powershell
sqlcmd -S <SQL_SERVER> -d <TENANT_DATABASE> -E -b `
  -v TenantRuntimePrincipal="<TENANT_DB_USER>" CentralRuntimePrincipal="" `
  -i .\deploy\permissions.sql
```

Use `WorkerDockRuntime` only in tenant databases and `WorkerDockCentralRuntime` only in the central database. The permissions script binds supplied existing principals to roles created by migrations; it does not create application tables or grants. `permissions.rollback.sql` removes only the explicitly supplied memberships. Review the migration ownership and SQL least-privilege guidance in [database setup](database.md) before deployment.

## Install a tenant

Prepare `tenant.json` from `tenant.example.json` and `worker.json` from `worker.example.json`, with matching tenant GUIDs. Then run the tenant installer with a certificate thumbprint already present in the local machine certificate store:

```powershell
pwsh -File .\deploy\Install-Tenant.ps1 `
  -InstanceId <TENANT_GUID> `
  -InstallRoot 'C:\Program Files\WorkerDock\<TENANT>' `
  -ConfigPath 'C:\WorkerDockConfig\<TENANT>\tenant.json' `
  -WorkerConfigPath 'C:\WorkerDockConfig\<TENANT>\worker.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-tenant-1.0.0.zip' `
  -SiteName 'WorkerDock-<TENANT>' `
  -HostName '<TENANT_HOSTNAME>' `
  -CertificateThumbprint '<CERTIFICATE_THUMBPRINT>'
```

The installer verifies all manifest file sizes and hashes, preserves existing per-tenant configuration, provisions an HTTPS IIS site with SNI enabled for shared port 443 and generic scheduler/worker services, and records the installed instance identity. Existing unrelated IIS names or conflicting identities cause a failure. Review and start services and the IIS site only after confirming the SQL connection, application configuration, and runtime permissions.

## Install the Control Center and additional workers

Install the Control Center with a separate database, config, package, site name, hostname, and certificate using `Install-Center.ps1`. Set `Central:Health:AllowedHosts` to the exact HTTPS tenant hostnames that the health probe may contact. The central vault adapter must resolve each registered tenant's health credential reference. A registered URL does not grant dashboard access; tenant authentication remains enforced by the tenant application.

```powershell
pwsh -File .\deploy\Install-Center.ps1 `
  -InstanceId <CENTER_GUID> `
  -InstallRoot 'C:\Program Files\WorkerDock\ControlCenter' `
  -ConfigPath 'C:\WorkerDockConfig\ControlCenter\center.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-center-1.0.0.zip' `
  -SiteName 'WorkerDock-ControlCenter' `
  -HostName '<CONTROL_CENTER_HOSTNAME>' `
  -CertificateThumbprint '<CERTIFICATE_THUMBPRINT>'
```

To add process capacity to a tenant, use a new worker installation GUID and service name with `Install-Worker.ps1` and a tenant package. Each generic service runs the same reviewed template catalog and gets its own configured host ceiling. Job definitions and per-template limits remain in the tenant database; installing a job never deploys code or starts a service.

```powershell
pwsh -File .\deploy\Install-Worker.ps1 `
  -InstanceId <TENANT_GUID> `
  -InstallRoot 'C:\Program Files\WorkerDock\<TENANT>' `
  -ConfigPath 'C:\WorkerDockConfig\<TENANT>\worker-02.json' `
  -PackagePath 'C:\WorkerDockPackages\workerdock-tenant-1.0.0.zip' `
  -ServiceName 'WorkerDock-<TENANT>-Worker02'
```

The Control Center and tenant install scripts preserve the protected config and Data Protection keys across reruns. Re-running with the same instance and release is idempotent; a conflicting identity or binding fails for review. The worker config must use a distinct `Worker:InstallationId` and the reviewed catalog digest.

## Verify and operate

Check `/health/ready` on each web application and inspect Windows service status and event logs before directing operators to the dashboard. Protect the IIS bindings with valid TLS, preserve each Data Protection key directory across upgrades, and grant each service only read/execute access to its release and the required database permissions. Keep worker execution directories writable only by their worker identity.

The scheduler and workers coordinate through the tenant SQL Server database and Quartz ADO job store. SQL Server is required for deployed instances; the memory adapter is for development and tests only. See [upgrade and restore procedure](upgrades.md) before replacing a release.

Run the portable release-script smoke checks with `pwsh -File .\tests\DeploymentSmoke.ps1`. It checks PowerShell syntax, JSON config shape, and package manifest hash acceptance and rejection. It does not provision IIS or verify SQL Server/Entra integration; those checks require the disposable Windows and SQL environment described in the release plan.
