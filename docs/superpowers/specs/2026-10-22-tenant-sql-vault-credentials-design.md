# Tenant SQL credentials through the vault

## Purpose

WorkerDock must connect to each tenant database without accepting a complete SQL connection string in configuration or an environment variable. Operators supply a SQL host, port, database name, SQL user, and logical password reference. Every SQL process retrieves the password through `ISecretVault` and constructs its connection string internally. Local development uses the explicitly mapped environment vault; production requires a registered real provider.

Success covers tenant web, scheduler, worker, database migrator, and upgrade. SQL profiles configured in the tenant app keep their current vault behavior. The Control Center and Development memory adapter keep their current storage behavior.

## Configuration contract

SQL runtime hosts read `Tenant:InstanceId`, `Tenant:Adapter=Sql`, and `Tenant:Sql:{Host,Port,Database,User,PasswordReference}`. The migrator reads the same five fields under `Migrator:Sql` with a separate deployment login and password reference, and keeps `WORKERDOCK_INSTANCE_ID` for tenant identity validation. The upgrade script receives a protected migrator config file through a new `-MigratorConfigPath` parameter. Direct migrator runs may use that file or equivalent .NET environment settings.

`Host` is a bare DNS name or IP address, without port, instance suffix, connection string syntax, or control characters. `Port` is 1–65535. `Database` and `User` are each 1–128 characters and contain no control characters. `PasswordReference` is a logical name of 1–128 ASCII letters, digits, underscores, dashes, or dots. A vault provider maps it to its own secret path. No vault path or environment variable name is selected by tenant API input.

`Tenant:ConnectionString` is rejected by tenant hosts and installers; `WORKERDOCK_SQL` is rejected by the migrator and upgrade script. Each is rejected even alongside the new settings. SQL hosts reject incomplete settings. Memory mode bypasses SQL resolution but still rejects a legacy tenant connection string.

## Components and flow

A shared infrastructure resolver validates the five fields, calls `ISecretVault.ResolveAsync`, and uses `SqlConnectionStringBuilder` to set TCP data source, initial catalog, SQL user and password. Encryption is mandatory, server certificate validation stays enabled, and `PersistSecurityInfo` is false. The resolver promptly disposes `SecretValue`. The constructed string is internal to the process and is never logged, serialized, returned by an API, or passed to a child process.

Tenant web resolves credentials before serving requests, then supplies one internal `TenantDatabaseOptions` to its SQL services. Scheduler and worker resolve before opening persistence and Quartz. The migrator resolves before SQL access. Existing stores and Quartz may continue to receive an internal connection string. A process restart is required after database password rotation. The SQL client retains a managed connection string during process lifetime, so disposing `SecretValue` cannot erase every password copy.

The environment vault is a Development substitute. It resolves only explicit mappings, for example `tenant.database.password` to an independently provisioned password environment variable. Tenant SQL startup accepts it only in Development with `WORKERDOCK_DEVELOPMENT_VAULT=1`. Production SQL startup requires a registered `ISecretVault` tied to that process identity and its approved reference namespace. Implementing a production provider is outside this change.

## Migrator and upgrade

Migrator `apply` and `rollback` use `Migrator:Sql` and the vault. Manifest checks, script checksums, SQL compatibility checks, tenant identity binding, and rollback guards remain.

`Upgrade-Instance.ps1` currently builds a SQL connection from the tenant runtime config for backup and migration-history checks, then passes it through `WORKERDOCK_SQL`. A new `prepare-upgrade tenant <target-migration> <backup-path>` migrator command takes the target migration and a new UNC backup path. It validates installed history against the package manifest, performs a copy-only backup, and verifies it before the upgrade script stages a release. The script retains its package, path, schema compatibility window, and release checks, then invokes the regular migrator apply command. Neither command emits credentials. The deployment login needs migration and backup rights. A failed preparation leaves the active release in place.

Tenant and worker installer validation requires the new nonsecret fields. The scheduler uses the tenant config. Persistent configuration and worker installation identity checks remain. An old persisted connection string must be replaced before upgrade.

## Failure behavior

Invalid or incomplete settings, a legacy full string, an unavailable secret, and SQL connectivity failure produce distinct stable safe codes. SQL hosts fail before accepting SQL work if a credential cannot be resolved. Errors, logs, command output, and readiness responses must not reveal passwords, constructed strings, environment variable names, or provider exception text. Cancellation stays cancellation. There is no fallback to integrated authentication, an old string, or another database.

## Documentation and verification

Update the SQL tutorial, SQL setup, tenant configuration, production deployment, upgrade, secrets, and configuration references, plus tenant and worker sample JSON. Local instructions show both sets of five nonsecret fields, explicit development secret mappings, Development environment setup, SQL TLS certificate setup, and restart on rotation. Production instructions state the real-provider requirement.

Focused tests cover required fields and legacy rejection, host and port validation, builder properties, approved lookup and redacted failure, eager startup in all SQL hosts, migrator commands, and installer and upgrade behavior. Disposable SQL tests verify migration and backup where the existing SQL fixture is available. Fixtures use synthetic passwords and never persist constructed connection strings.



