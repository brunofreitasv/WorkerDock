# Configuration reference

Production configuration is normally kept in protected JSON files copied from `deploy/config/tenant.example.json`, `worker.example.json` and `center.example.json`. Files are stored under the installation root's `config` directory and retained across installer reruns and upgrades. A .NET environment variable uses double underscore (`__`) for each nested section. For example, `Tenant__ConnectionString` maps to `Tenant.ConnectionString`.

`WORKERDOCK_DEVELOPMENT_VAULT=1` enables the explicitly mapped environment-backed development secret provider; it is not a production vault integration. The tenant hosts currently register this development provider, which otherwise resolves no secrets. A production deployment must register its own `ISecretVault` adapter in every process that needs one. The Control Center rejects a missing production adapter at startup. See [secret integration](../secrets.md).

| Host | Configuration keys | Notes |
| --- | --- | --- |
| Tenant web | `Tenant:InstanceId`, `Tenant:ConnectionString`, `Tenant:Adapter`; `Access:*`; `DataProtection:KeyDirectory`; `Health:CredentialReference`; `Retention:ArchiveRoot` | SQL adapter in production. Optional Entra uses Authority, ClientId and ClientSecretReference together. |
| Scheduler service | `Tenant:InstanceId`, `Tenant:ConnectionString`, `Retention:ArchiveRoot` | One per tenant. Runs schedules, projections, alerts and retention. |
| Worker service | `Tenant:InstanceId`, `Tenant:ConnectionString`; `Worker:InstallationId`, `Worker:CatalogDigest`, `Worker:HostCeiling`, `Worker:DrainSeconds`; `Executor:Path`, `Executor:Arguments` | Unique worker installation GUID per service. Installer sets executor path to its release. |
| Control Center web | `Central:InstanceId`, `Central:ConnectionString`, `Central:Adapter`; `Central:Health:AllowedHosts`, `Central:Health:AllowedPrivateCidrs`; `Access:*`; `DataProtection:KeyDirectory` | Separate database, identity and key directory. AllowedHosts is mandatory. |

## Authentication keys

`Access.Authority` must be the exact HTTPS issuer authority for the directory; common, organizations and consumers authorities are rejected. `Access.ClientId` is the audience. `Access.ClientSecretReference` names the protected bootstrap client secret; the current bootstrap adapter reads only `WORKERDOCK_BOOTSTRAP_<reference>`. Reference syntax is uppercase ASCII letters, digits and underscore, 1–128 characters. Supply all three keys or omit all three. `Access.RequiredManagementRole` is optional and case-sensitive. `Access.RoleClaimType` defaults to `roles`.

No authentication settings means anonymous read and management. Entra without a required role means authenticated read and management. Entra plus required role means any accepted user can read and only the matching role can manage. See [authentication](../authentication.md).

## Worker and scheduler settings

`Worker.InstallationId` must be a nonempty GUID unique to a worker service; changing it identifies a different installed worker. `Worker.CatalogDigest` must match the aggregate digest for the reviewed descriptors installed in that package. `Worker.HostCeiling` is the service process's host capacity ceiling. `Worker.DrainSeconds` is constrained to 1 through 60. `Executor.Path` and arguments are deployment-authored and never job-supplied. The default worker example uses a ceiling of 10 and 40-second drain period.

The scheduler uses `Retention.ArchiveRoot` for archive output. If set, this must be an absolute directory writable only by the scheduler service identity and administrators.

## Control Center health probe

`Central.Health.AllowedHosts` is a nonempty list of DNS hostnames permitted for health checks. `Central.Health.AllowedPrivateCidrs` is an optional explicit list of private network ranges; private destinations are otherwise denied. A tenant registration accepts only an HTTPS base URL on port 443 without credentials, query or fragment, an expected instance GUID and a logical credential reference matching `[A-Z0-9_]{1,128}`. The center sends the resolved value only in `X-WorkerDock-Health` to the fixed `/health/ready` path. Health credentials are resolved by the center's production `ISecretVault` adapter; no value is persisted in the central database.

## SQL and migration variables

The database migrator accepts `WORKERDOCK_SQL` and `WORKERDOCK_INSTANCE_ID`, then the command `apply|rollback tenant|central <migration>`. It checks migration checksums and server/database compatibility before applying schema changes. See [database setup and migrations](../how-to/setup-sql-server.md) for exact SQL commands, IDs, role grants and rollback constraints. Production upgrades use [Upgrade-Instance.ps1](../how-to/deploy-production.md) to back up the SQL database and application package, verify the compatibility window and apply package migrations.
