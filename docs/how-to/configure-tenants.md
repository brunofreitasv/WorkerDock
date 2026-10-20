# Configure a tenant and register it in Control Center

Each tenant has a fixed instance GUID, one SQL database and separate runtime configuration for the IIS web app, scheduler and each worker service. The configuration examples in `deploy/config/` are the authoritative starting point for production JSON files. Protect these files with filesystem ACLs and keep them outside the release package.

## Tenant settings

`tenant.example.json` supplies the tenant web and scheduler settings:

| Setting | Purpose |
| --- | --- |
| `Tenant.InstanceId` | Stable tenant GUID, matching `-InstanceId` and every worker config. |
| `Tenant.ConnectionString` | Tenant database connection using the restricted runtime identity; use encryption and certificate validation. |
| `Tenant.Adapter` | Production SQL adapter (`Sql`). Memory mode is Development-only. |
| `Access.Authority`, `Access.ClientId`, `Access.ClientSecretReference` | Optional Entra confidential app configuration. Supply all three or omit all three. |
| `Access.RequiredManagementRole`, `Access.RoleClaimType` | Optional case-sensitive role required for management; default role claim is `roles`. |
| `DataProtection.KeyDirectory` | Absolute, persistent directory protected for the IIS application identity. |
| `Health.CredentialReference` | Uppercase logical reference used by the tenant `/health/ready` credential check. |
| `Retention.ArchiveRoot` | Optional absolute archive directory writable by the scheduler identity. |

`worker.example.json` supplies per-service settings. Its `Tenant.InstanceId` and connection must target the same tenant. `Worker.InstallationId` is a stable, unique GUID per worker service; `CatalogDigest` is the SHA-256 aggregate for the reviewed installed template catalog; `HostCeiling` sets that process's maximum host capacity; `DrainSeconds` accepts 1–60 seconds. `Executor.Path` is rewritten by the installer to the package's Windows executor path. `Executor.Arguments` is deployment-owned. The scheduler reads only the tenant database settings and is not separately configured as a worker.

For IIS and services, the installers copy the JSON files under `<InstallRoot>\config`, preserve them on rerun, and generate release-specific `appsettings` from those persistent files. Editing the original source file after install does not update the installed copy. Make configuration changes to the protected installed file and restart the affected process. Each additional worker receives its own JSON file and unique installation ID.

Configuration can also be supplied through .NET environment variables, where `__` represents a section separator: for example `Tenant__InstanceId`, `Tenant__ConnectionString`, `Access__Authority`, `Worker__HostCeiling`, and `Executor__Path`. The deployment installers load protected JSON configuration; service environment variables must be set in the service's protected environment by deployment tooling. Do not place credentials in command lines, shell history or source control.

## Authentication and views

Authentication is configured separately for each tenant and for the Control Center. With Entra configured, every valid user accepted by that app audience can read. If `RequiredManagementRole` is set, only a user with that exact role claim can manage; viewers can use read-protected views but receive 403 for direct management API calls. Omit the role to let any authenticated user manage. Omit all three authentication bootstrap settings and the management role for anonymous mode; anonymous users can both read and manage, so restrict reachability at the network boundary. A role without complete Entra configuration or partial Entra configuration fails startup. See [authentication and access policies](../authentication.md) for app callbacks, role claim configuration and secret injection.

Monitoring and audit views use the tenant's Read policy; management actions and configuration use Manage. An unavailable data store produces an unavailable response, not an empty result. Control Center membership does not grant access to tenant dashboards. Opening a tenant link sends the user to the tenant's own authentication boundary.

## Health credentials and Control Center registration

The center stores a tenant's display name, expected tenant GUID, dashboard URL and logical health credential reference. Register a base HTTPS URL such as `https://tenant-a.example.com/` (port 443, without query or fragment), the tenant `InstanceId`, and the reference configured by `Health.CredentialReference`. The center probes only `/health/ready`; it sends the resolved secret in `X-WorkerDock-Health`. It rejects redirects, unapproved hosts, non-443 ports and mismatched returned instance GUIDs.

`Central.Health.AllowedHosts` must enumerate the exact DNS hostnames that this center may contact. For example:

```json
"Health": {
  "AllowedHosts": [ "tenant-a.example.com", "tenant-b.example.com" ],
  "AllowedPrivateCidrs": []
}
```

It is required at startup. Internal/private destinations additionally require explicit approved CIDR ranges in `AllowedPrivateCidrs`. Health refresh requires authentication if Entra is configured; it is available anonymously only when center authentication is disabled. A successful observation becomes stale after two minutes.

The production center must provide its own `ISecretVault` implementation in the Control Center composition root. The development environment vault is intentionally rejected in Production. Bind the vault to a center-only service identity and allow only the registered health references. For a simple environment-backed deployment adapter, explicitly map logical references to named environment variables through protected configuration; never derive variable names from tenant input. The tenant health credential itself belongs in that protected secret store, not in the registration database or config JSON. The center must not be able to read tenant database or tenant vault secrets. See [secrets](../secrets.md) and [Control Center](../control-center.md).

## Separate identity and storage

Give each tenant its own Entra app registration, SQL database, runtime identity and Data Protection key directory. Give the center its own Entra app registration, protected state directory, Data Protection key directory, and vault identity. Web, scheduler and worker service identities only read their respective configs and key material as required. Use a tenant-specific SQL runtime principal assigned to `WorkerDockRuntime`; the center has no SQL runtime principal. Only the deployment migrator needs tenant schema creation rights. See [SQL setup](setup-sql-server.md) for migrations and runtime grants.
