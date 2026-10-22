# Set up SQL Server

WorkerDock tenant databases require SQL Server 2022 or newer with database compatibility level 160. Provision one separate database for each tenant. The Control Center uses protected files and has no SQL Server requirement.

## Runtime and test databases

```mermaid
flowchart LR
    Operator["Operator chooses a preparation path"]
    Manifest["Versioned tenant migration manifest"] --> MigrationScript["Explicit target via Invoke-TenantMigration.ps1"]
    Snapshot["Fresh-only current-state.sql snapshot"]
    Operator --> MigrationScript
    Operator --> Snapshot
    MigrationScript --> TenantDb[("Tenant database<br/>[workerdock] + Quartz tables")]
    Snapshot --> TenantDb
    TenantApps["Tenant web, scheduler, workers"] -->|"restricted WorkerDockRuntime role"| TenantDb
    CenterApp["Optional Control Center"] --> CenterFiles[("Protected state directory")]
    subgraph Tests["Disposable SQL integration tests only"]
        Fixtures["tests/sql + integration fixture scripts"] --> TestDb[("Generated WorkerDock_Test_* database")]
    end
```

For a tenant database, the operator chooses one preparation path: apply the
ordered, checksummed migrations listed in `database/tenant/manifest.json` with
an explicit target using `scripts/Invoke-TenantMigration.ps1`, or apply
`database/tenant/current-state.sql` to a fresh empty database. The snapshot is
fresh-install-only and must never be applied to an existing tenant or used as
an upgrade. These paths are design alternatives; live SQL parity has not been
verified. The `tests/sql` scripts create only fixture objects in generated
disposable databases; never apply them to a runtime tenant. Quartz's attributed
upstream source is retained for provenance and is not installed independently.
The in-memory tutorial needs none of these SQL objects.

### Fresh database snapshot

Use `database/tenant/current-state.sql` only for a newly created, empty tenant
database at compatibility level 160 or later. Generate the stable tenant GUID
that Web, Scheduler, and Worker will share. In a working copy of the snapshot,
replace every `__WORKERDOCK_INSTANCE_ID__` token with that GUID. If the runtime
SQL login already exists on the server, edit the marked `CREATE USER` and
`ALTER ROLE WorkerDockRuntime ADD MEMBER` statements to bind its database user.
These statements belong in the same snapshot action so its role membership is
committed or rolled back with the schema.

Send the complete rendered file to the selected database as one SQL batch. Do
not split it at `GO` separators or execute it in SQLCMD mode; the file contains
its own transaction and rejects an ambient transaction. Its history rows are
an equivalent satisfied state baseline, not a record that each earlier
migration script ran. The snapshot represents the final state only and cannot
upgrade an existing database. Live SQL parity remains unverified.

## Provision a tenant database

As a SQL Server administrator, create an empty database and set its compatibility level:

```sql
CREATE DATABASE [WorkerDockTenant];
ALTER DATABASE [WorkerDockTenant] SET COMPATIBILITY_LEVEL = 160;
```

Provision a separate migration identity with permission to create schemas,
tables, procedures and roles and to grant permissions in this dedicated
database. A temporary `db_owner` membership is one way to supply these
installation rights; remove it after migration. Provision a separate long-lived
application identity as an existing database user. The application identity
must not be a database owner or receive DDL rights. Use your organization's
approved SQL or Windows identity process; deployment scripts do not create
server logins or database users.

From a PowerShell session at the repository root, apply migrations with an explicit target from
`database/tenant/manifest.json`. Build or publish the Web application so the
assembly directory contains `WorkerDock.Web.dll`, its dependency manifest, and
`WorkerDock.Infrastructure.dll`; for example, use `dotnet publish` with your
selected configuration. Supply a newly generated stable tenant GUID and the
separate deployment migration credential:

The PowerShell host must run on .NET 10 or later; PowerShell 7 alone is not
sufficient when its host is bound to an earlier .NET runtime. Check
`[System.Environment]::Version` in the session before invoking the script.

```powershell
$credential = Get-Credential '<TENANT_MIGRATION_LOGIN>'
$assemblyDirectory = (Resolve-Path 'src/WorkerDock.Web/bin/Release/net10.0').Path
& scripts/Invoke-TenantMigration.ps1 `
  -Action Apply `
  -SqlHost '<SQL_SERVER_FQDN>' -Port 1433 -Database '<TENANT_DATABASE>' `
  -Credential $credential -InstanceId '<stable-tenant-guid>' `
  -TargetMigration '0010_remove_builtin_v1' `
  -ScriptsRoot (Resolve-Path 'database').Path `
  -AssemblyDirectory $assemblyDirectory
```

Select the target appropriate for the reviewed release; do not copy an example
ID without checking the manifest. The script applies earlier migrations in
order, verifies script checksums and schema history, checks server version and
compatibility level, and binds the database to the supplied instance ID. Keep
the migration identity separate from the long-lived runtime identity and remove
its temporary DDL grants after migration.

`WORKERDOCK_DEVELOPMENT_VAULT` and its explicit secret map are for local Development only. Production must follow the organization's approved protected credential process for the migration identity. The script prompts through a PowerShell credential object; do not place credentials in command lines, shell history or source control.

The script applies every earlier migration in `database/tenant/manifest.json`, verifies both script checksums, checks server version and compatibility level, records migration history, and binds the database to the supplied instance ID. For a later release, apply its newest manifest migration ID. The migration identity needs DDL rights only during migration.

After migration, ensure the tenant application identity is mapped to a
database user, then add that principal to the runtime role. From the repository
root, connect as an administrator with Windows authentication and replace the
server and database principal placeholders:

```powershell
sqlcmd -S '<SQL_SERVER>' -d WorkerDockTenant -E -b `
  -v TenantRuntimePrincipal="<TENANT_DB_USER>" `
  -i .\database\tenant\permissions.sql
```

`database/tenant/permissions.sql` adds a supplied tenant principal to `WorkerDockRuntime`; the tenant migration defines the grants. The tenant role contains the application's required data access, including DML for both Quartz stores. It does not grant schema changes. To remove an explicit role membership:

```powershell
sqlcmd -S '<SQL_SERVER>' -d WorkerDockTenant -E -b `
  -v TenantRuntimePrincipal="<TENANT_DB_USER>" `
  -i .\database\tenant\permissions.rollback.sql
```

Configure each tenant web, scheduler and worker host with `Tenant:Adapter=Sql`, the same `Tenant:InstanceId`, and `Tenant:Sql:Host`, `Port`, `Database`, `User` and `PasswordReference`. Use the restricted application identity and an explicitly mapped logical reference. SQL connections require encryption and valid server certificate verification; install the issuing CA/server certificate in the host trust store. Do not enable trust-server-certificate bypasses. The deployment tooling configures services and hosts; SQL setup and migrations are an explicit operator step. Restart affected processes after rotating a vault password so new connections use it.

## Optional current Control Center

The current center uses `Central:InstanceId` and an absolute
`Central:Storage:DataDirectory` outside release and web directories. Protect
the directory so only the center IIS application-pool identity and
administrators can modify it, and keep it outside web serving. No central SQL
database, migration, SQL identity, or SQL connection string is required. See
[Control Center setup](configure-control-center.md) for persistent storage and
health-probe configuration. Tenant SQL setup above remains required for each
tenant.

## Roll back a database

Take and verify a backup first. Rollbacks run one migration at a time and are guarded against discarding populated state. Stop all web, scheduler, worker, and publisher processes before rollback. For a truly empty disposable tenant database, remove the runtime membership, then run:

```powershell
$credential = Get-Credential '<TENANT_MIGRATION_LOGIN>'
& scripts/Invoke-TenantMigration.ps1 `
  -Action Rollback -SqlHost '<SQL_SERVER_FQDN>' -Port 1433 `
  -Database '<TENANT_DATABASE>' -Credential $credential `
  -InstanceId '<same-tenant-guid>' -TargetMigration '<explicit-migration-id>' `
  -ScriptsRoot (Resolve-Path 'database').Path `
  -AssemblyDirectory (Resolve-Path 'src/WorkerDock.Web/bin/Release/net10.0').Path
```

Continue with preceding IDs only when their down scripts' empty-state requirements are satisfied. For a populated database, restore a backup or perform a separately planned data migration; do not bypass a rollback guard. The Control Center has no SQL rollback because it stores its state in protected files.

## SQL integration tests

Only run SQL integration tests against an isolated, disposable SQL Server instance. The `WORKERDOCK_TEST_SQL` identity needs permission to create and drop databases in `master`; fixtures generate `WorkerDock_Test_<32 hex digits>`, set compatibility 160, and drop only that generated database. Quartz process tests additionally require a distinct restricted SQL login in `WORKERDOCK_TEST_SQL_RUNTIME`. SQL target-template tests use a third distinct restricted login in `WORKERDOCK_TEST_SQL_TARGET_RUNTIME`. All logins must point to the same disposable server. Never use a production server or a database that contains data to preserve.

```powershell
$env:WORKERDOCK_TEST_SQL='<disposable-server-provisioner-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-restricted-runtime-login>'
$env:WORKERDOCK_TEST_SQL_TARGET_RUNTIME='<same-server-restricted-target-login>'
dotnet test tests/WorkerDock.IntegrationTests -c Release
```

The fixtures own their generated databases and test objects. Their test-only SQL lives under `tests/sql` and `tests/WorkerDock.IntegrationTests/Sql`; it is not part of runtime provisioning. Missing SQL configuration causes the SQL fixtures to report `SQL_TEST_BLOCKED`. Record actual runs in the [release evidence](../validation/release-evidence.md).

See [database object reference](../reference/database-objects.md), [Control Center setup](configure-control-center.md), and [Quartz integration](../reference/quartz-integration.md) for object inventories and design details.
