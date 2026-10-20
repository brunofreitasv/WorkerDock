# Control Center

The Control Center is an optional ASP.NET Core application with its own React entry point. It keeps tenant links, advisory health observations, and append-only audit metadata in a protected local file journal. It does not create tenants, change tenant configuration, or read tenant jobs. Each link opens the tenant dashboard directly; the tenant's own authentication and authorization still apply.

## Current setup

A fresh Control Center needs no central SQL Server database, SQL schema, SQL connection string, or database migrator. Configure a stable, nonempty `Central:InstanceId` and an absolute `Central:Storage:DataDirectory` outside the release and web directories. For example, use a persistent path such as `C:\ProgramData\WorkerDock\ControlCenter\state`; keep it across releases. The center writes its registry journal and advisory health snapshot there. Give Modify access only to the center IIS application-pool identity and administrators, and prevent the web server from serving the directory. Use one active center process per directory.

The production file adapter is the default. In disposable Development runs, the in-memory adapter can be selected explicitly:

```powershell
$env:Central__Adapter='Memory'
$env:Central__InstanceId='<stable-center-guid>'
$env:Central__Health__AllowedHosts__0='tenant-a.example.com'
```

For persistent operation, configure `Central__Storage__DataDirectory` to the absolute state path instead of selecting Memory. Keep `Central__InstanceId` unchanged when restarting or restoring the same center. Configure the center's allowed health hosts and its production vault adapter as described below. A center restore uses a verified file backup and the same instance ID; health observations are advisory and can be stale.

## Tenant links and health

The center stores tenant display names, HTTPS dashboard URLs, expected instance IDs, logical references to health-only credentials, and safe health/audit metadata. Resolved credentials and secret-bearing URLs are not written to files, logs, audit, metrics, or API responses. A tenant registration accepts only an HTTPS base URL on port 443 without credentials, query or fragment, an expected instance GUID, and a logical credential reference matching `[A-Z0-9_]{1,128}`.

`Central:Health:AllowedHosts` is mandatory and must list approved tenant hostnames; an empty allowlist prevents health probing. Internal/private destinations also need explicit `Central:Health:AllowedPrivateCidrs` ranges. Probes validate resolved addresses, reject redirects, proxies and cookies, and request only the fixed `/health/ready` endpoint. The returned instance ID must match the registered expected ID. Health is advisory: a successful observation becomes `Stale` after two minutes. Refresh is authenticated when Entra is configured, available anonymously only when authentication is disabled, rate-limited, and protected against CSRF.

The development environment vault is disabled unless `WORKERDOCK_DEVELOPMENT_VAULT=1`; it resolves only explicit logical-reference-to-environment-variable mappings. Production startup requires an organization-provided `ISecretVault` adapter. Bind it to a center-only identity and restrict its reference allowlist to health credentials. Tenant databases and credentials must not be accessible to this identity.

The Control Center uses its own Entra app registration, cookie prefix, data-protection discriminator, and key directory. Configure its `Access:Authority`, `Access:ClientId`, and protected `Access:ClientSecretReference` as described in [authentication](authentication.md). Any authenticated user accepted by that audience may read the directory and refresh health. If `Access:RequiredManagementRole` is configured, only its configured role claim may register, edit, or remove links; if omitted, any authenticated user may manage. With no authentication configuration, access is anonymous, including management. Restrict anonymous deployments at the network boundary. The same policies protect the management API, not just the React controls.

## Import a legacy SQL-backed center

This procedure is only for an existing center that used the legacy central SQL schema. Back up and verify the old database, stop the old center, and use a read-only SQL identity for import. Set `WORKERDOCK_LEGACY_CENTRAL_SQL` to the legacy central database connection and `WORKERDOCK_INSTANCE_ID` to its stable center GUID. From the repository root, run:

```powershell
dotnet run --project src/WorkerDock.CentralImport -- import --data-directory '<absolute-path>' --instance-id $env:WORKERDOCK_INSTANCE_ID
```

The target directory must be empty and protected. The importer reads legacy center records and writes a journal for the same instance. It preserves registration identifiers, nonsecret fields, timestamps, safe audit history, and latest health observations; journal ETags are new, so clients must refetch them. Review the import receipt without exposing credential references or secret values. Deploy the file-backed center side by side, verify tenant links and health, then move the HTTPS binding. Keep the old database and package for a deliberate rollback; file-side changes after cutover do not flow back to SQL.

## Legacy database objects and rollback

The historical central SQL migrations under [`database/central`](../database/central) remain for legacy restore and import only. They are not part of fresh Control Center setup and must not be run for a new center. See [database objects](reference/database-objects.md) for the legacy object inventory. Tenant SQL migrations and the tenant's two Quartz stores remain required and unchanged.

There is no dedicated central Windows service. Host the Control Center web application separately (for example, as its own IIS application); tenant schedulers and worker services continue to run independently if the Control Center is unavailable.
