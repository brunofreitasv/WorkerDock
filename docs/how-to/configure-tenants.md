# Configure a tenant and register it in Control Center

Each tenant has a fixed instance GUID, one SQL database, a web app, and one runtime service configuration. The configuration examples in `examples/config/` are the authoritative starting point for production JSON files. Protect these files with filesystem ACLs and keep them outside the release package.

## Tenant settings

`tenant.example.json` supplies the tenant web settings:

| Setting | Purpose |
| --- | --- |
| `Tenant.InstanceId` | Stable tenant GUID, matching `-InstanceId` in the runtime configuration. |
| `Tenant.Sql.Host`, `Tenant.Sql.Port`, `Tenant.Sql.Database` | SQL Server endpoint and tenant database. Use a DNS name or IP without an instance suffix. |
| `Tenant.Sql.User`, `Tenant.Sql.PasswordReference` | Restricted runtime login and logical password reference resolved by the configured vault. Never store the password in config. |
| `Tenant.Adapter` | Production SQL adapter (`Sql`). Memory mode is Development-only. |
| `Access.Authority`, `Access.ClientId`, `Access.ClientSecretReference` | Optional Entra confidential app configuration. Supply all three or omit all three. |
| `Access.RequiredManagementRole`, `Access.RoleClaimType` | Optional case-sensitive role required for management; default role claim is `roles`. |
| `DataProtection.KeyDirectory` | Absolute, persistent directory protected for the IIS application identity. |
| `Health.CredentialReference` | Uppercase logical reference used by the tenant `/health/ready` credential check. |
| `Retention.ArchiveRoot` | Optional absolute archive directory writable by the runtime service identity. |

`worker.example.json` supplies the single runtime service settings. Its `Tenant.InstanceId` and five `Tenant.Sql` fields must target the same tenant database as the web app. The runtime derives the installed template catalog digest from its trusted descriptors and registers their immutable metadata in the tenant database. `Worker.HostCeiling` sets the maximum concurrent child count from 1 through 1000; `Worker.DrainSeconds` accepts 1–60 seconds. `Executor.Path` must point to the selected release's executor binary. `Executor.Arguments` is deployment-owned. A tenant-scoped mutex rejects a second runtime service for that tenant.

Your deployment tooling should copy protected JSON files to the configured host paths and preserve them across releases. Make configuration changes to the deployed protected file and restart the affected process. Use one runtime service configuration per tenant.

Configuration can also be supplied through .NET environment variables, where `__` represents a section separator: for example `Tenant__InstanceId`, `Tenant__Sql__Host`, `Tenant__Sql__Port`, `Tenant__Sql__Database`, `Tenant__Sql__User`, and `Tenant__Sql__PasswordReference`. Deployment tooling loads protected JSON configuration; service environment variables must be set in the service's protected environment by deployment tooling. SQL connections require encryption and valid server certificate verification; install the issuing CA/server certificate in the host trust store. Do not place credentials in command lines, shell history or source control. Restart affected hosts after rotating a password in the vault.

## Authentication and views

Authentication is configured separately for each tenant and for the Control Center. With Entra configured, every valid user accepted by that app audience can read. If `RequiredManagementRole` is set, only a user with that exact role claim can manage; viewers can use read-protected views but receive 403 for direct management API calls. Omit the role to let any authenticated user manage. Omit all three authentication bootstrap settings and the management role for anonymous mode; anonymous users can both read and manage, so restrict reachability at the network boundary. A role without complete Entra configuration or partial Entra configuration fails startup. See [authentication and access policies](configure-authentication.md) for app callbacks, role claim configuration and secret injection.

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

The production center must provide its own `ISecretVault` implementation in the Control Center composition root. The development environment vault is intentionally rejected in Production. Bind the vault to a center-only service identity and allow only the registered health references. For a simple environment-backed deployment adapter, explicitly map logical references to named environment variables through protected configuration; never derive variable names from tenant input. The tenant health credential itself belongs in that protected secret store, not in the registration database or config JSON. The center must not be able to read tenant database or tenant vault secrets. See [secrets](../reference/secrets.md) and [Control Center](configure-control-center.md).

## Separate identity and storage

Give each tenant its own Entra app registration, SQL database, runtime identity and Data Protection key directory. Give the center its own Entra app registration, protected state directory, Data Protection key directory, and vault identity. Web and runtime service identities only read their respective configs and key material as required. Use a tenant-specific SQL database user that already exists in the target database; setup validates and grants this user directly. The center has no SQL runtime principal. Only the separately controlled migration credential needs tenant schema creation rights. See [SQL setup](setup-sql-server.md) for migration and setup commands.
