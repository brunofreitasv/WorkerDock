# Configuration reference

Production configuration is normally kept in protected JSON files copied from `examples/config/tenant.example.json`, `worker.example.json` and `center.example.json`. Files are stored under the protected deployment storage and are loaded by the deployment tooling. A .NET environment variable uses double underscore (`__`) for each nested section. Tenant SQL fields are `Tenant:Sql:Host`, `Port`, `Database`, `User` and `PasswordReference`; tenant hosts also require `Tenant:Adapter=Sql` and `Tenant:InstanceId`. The corresponding environment names are `Tenant__Sql__Host`, `Tenant__Sql__Port`, `Tenant__Sql__Database`, `Tenant__Sql__User` and `Tenant__Sql__PasswordReference`.

`WORKERDOCK_DEVELOPMENT_VAULT=1` enables the explicitly mapped environment-backed development secret provider; it is not a production vault integration. The tenant hosts currently register this development provider, which otherwise resolves no secrets. A production deployment must register its own `ISecretVault` adapter in every process that needs one. The Control Center rejects a missing production adapter at startup. See [secret integration](secrets.md).

| Host | Configuration keys | Notes |
| --- | --- | --- |
| Tenant web | `Tenant:InstanceId`, `Tenant:Adapter`, `Tenant:Sql:{Host,Port,Database,User,PasswordReference}`; `Access:*`; `DataProtection:KeyDirectory`; `Health:CredentialReference`; `Retention:ArchiveRoot` | SQL adapter in production. Optional Entra uses Authority, ClientId and ClientSecretReference together. |
| Tenant runtime service | `Tenant:InstanceId`, `Tenant:Adapter`, `Tenant:Sql:{Host,Port,Database,User,PasswordReference}`; `Worker:CatalogDigest`, `Worker:HostCeiling`, `Worker:DrainSeconds`; `Executor:Path`, `Executor:Arguments`; `Retention:ArchiveRoot` | One service per tenant. Polls SQL for schedules and work; launches at most `HostCeiling` child processes. |
| Control Center web | `Central:InstanceId`, `Central:Storage:DataDirectory`; `Central:Adapter` (Development only for Memory); `Central:Health:AllowedHosts`, `Central:Health:AllowedPrivateCidrs`; `Access:*`; `DataProtection:KeyDirectory` | Stable center identity and absolute protected state directory outside release/web roots. AllowedHosts is mandatory. No central SQL connection is used. |

## Authentication keys

`Access.Authority` must be the exact HTTPS issuer authority for the directory; common, organizations and consumers authorities are rejected. `Access.ClientId` is the audience. `Access.ClientSecretReference` names the protected bootstrap client secret; the current bootstrap adapter reads only `WORKERDOCK_BOOTSTRAP_<reference>`. Reference syntax is uppercase ASCII letters, digits and underscore, 1–128 characters. Supply all three keys or omit all three. `Access.RequiredManagementRole` is optional and case-sensitive. `Access.RoleClaimType` defaults to `roles`.

No authentication settings means anonymous read and management. Entra without a required role means authenticated read and management. Entra plus required role means any accepted user can read and only the matching role can manage. See [authentication](../how-to/configure-authentication.md).

## Runtime settings

`Worker.CatalogDigest` must match the aggregate digest for reviewed descriptors installed in the runtime package. `Worker.HostCeiling` is the tenant's maximum concurrent child count and accepts values from 1 through 1000; the example uses 10. `Worker.DrainSeconds` is constrained to 1 through 60; the example uses 40 seconds. The runtime holds a tenant-scoped named mutex and rejects a second service instance. It polls due schedules every second, executions every 500 ms, alert rules and metric aggregation every five seconds, deliveries every two seconds, zone rules and retention every minute. These intervals are bounded polling cadence, not exact start-time guarantees. `Executor.Path` and arguments are deployment-authored and never job-supplied.

Dynamic-value plans are job-revision data, not host configuration or secrets. A plan stores its resolver version, timezone, declared JSON Pointer targets, sources, and transformations. Execution context and resolved nonsecret values are stored with the execution. No additional environment variable, vault key, or deployment credential is needed to enable the built-in dynamic sources. See [job execution guarantees](execution-guarantees.md) for source semantics and retry behavior.

The runtime uses `Retention.ArchiveRoot` for archive output. If set, this must be an absolute directory writable only by the runtime service identity and administrators.

## Control Center health probe

`Central.Storage.DataDirectory` is an absolute persistent path outside versioned release and web directories. Restrict write access to the center IIS application-pool identity and administrators, and keep the directory out of web serving. `Central.InstanceId` is a stable, nonempty GUID and must remain the same when restoring that center. Use one active center process per state directory. A fresh center uses the file registry and has no central SQL database or SQL identity requirement. `Central:ConnectionString` must be absent or empty; a nonempty value is rejected. `Central.Adapter=Memory` is for disposable Development use only.

`Central.Health.AllowedHosts` is a nonempty list of DNS hostnames permitted for health checks. `Central.Health.AllowedPrivateCidrs` is an optional explicit list of private network ranges; private destinations are otherwise denied. A tenant registration accepts only an HTTPS base URL on port 443 without credentials, query or fragment, an expected instance GUID and a logical credential reference matching `[A-Z0-9_]{1,128}`. The center sends the resolved value only in `X-WorkerDock-Health` to the fixed `/health/ready` path. Health credentials are resolved by the center's production `ISecretVault` adapter; no value is persisted in the journal.

## SQL and migration variables

The optional `scripts/Invoke-TenantMigration.ps1` accepts the SQL host, port, database, a PowerShell credential, existing `-RuntimePrincipal`, either `-InitialSetup` or stable `-InstanceId`, explicit target migration, database script root, and reviewed Web assembly directory. The selected database user must already exist in the target database; the script validates and grants it. Save the GUID printed after successful initial setup for `Tenant.InstanceId`; reuse the same value with `-InstanceId`. Keep the migration credential separate from the runtime SQL identity and provide it through an approved protected process. See [database setup and migrations](../how-to/setup-sql-server.md) for the snapshot token requirements and commands.
