# Set up SQL Server

WorkerDock supports SQL Server 2022 or newer with database compatibility level 160. Provision a separate database for each tenant and a separate central database if using Control Center. Do not combine central and tenant data.

## Provision a tenant database

As a SQL Server administrator, create an empty database and set its compatibility level:

```sql
CREATE DATABASE [WorkerDockTenant];
ALTER DATABASE [WorkerDockTenant] SET COMPATIBILITY_LEVEL = 160;
```

Provision a deployment migrator identity with permission to create schemas,
tables, procedures and roles and to grant permissions in this dedicated
database. A temporary `db_owner` membership is one way to supply these
installation rights; remove it after migration. Provision a separate long-lived
application identity as an existing database user. The application identity
must not be a database owner or receive DDL rights. Use your organization's
approved SQL or Windows identity process; deployment scripts do not create
server logins or database users.

Apply the complete current tenant migration chain from the repository root. Supply a newly generated, stable GUID for this tenant and a migrator connection string targeting the database. Keep secrets in protected deployment configuration:

```powershell
$env:WORKERDOCK_INSTANCE_ID='<stable-tenant-guid>'
$env:WORKERDOCK_SQL='<tenant-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0008_retention
```

The runner applies every earlier migration in `database/tenant/manifest.json`, verifies both script checksums, checks server version and compatibility level, records migration history, and binds the database to the supplied instance ID. For a later release, apply its newest manifest migration ID. The migrator needs DDL rights only during migration.

After migration, ensure the tenant application identity is mapped to a
database user, then add that principal to the runtime role. From the repository
root, connect as an administrator with Windows authentication and replace the
server and database principal placeholders:

```powershell
sqlcmd -S '<SQL_SERVER>' -d WorkerDockTenant -E -b `
  -v TenantRuntimePrincipal="<TENANT_DB_USER>" CentralRuntimePrincipal="" `
  -i .\deploy\permissions.sql
```

`deploy/permissions.sql` only adds a supplied principal to `WorkerDockRuntime` or `WorkerDockCentralRuntime`; versioned migrations define the grants. The tenant role contains the application's required data access, including DML for both Quartz stores. It does not grant schema changes. To remove an explicit role membership:

```powershell
sqlcmd -S '<SQL_SERVER>' -d WorkerDockTenant -E -b `
  -v TenantRuntimePrincipal="<TENANT_DB_USER>" CentralRuntimePrincipal="" `
  -i .\deploy\permissions.rollback.sql
```

Configure `Tenant__Adapter=Sql`, `Tenant__InstanceId` with the same GUID, and `Tenant__ConnectionString` with the application identity. The deployment package installer configures services and IIS; it does not create databases or run migrations.

## Provision the optional central database

Create a different empty database at compatibility level 160:

```sql
CREATE DATABASE [WorkerDockCentral];
ALTER DATABASE [WorkerDockCentral] SET COMPATIBILITY_LEVEL = 160;
```

Map temporary migrator and long-lived application identities to database users. Apply the central migration with its own stable instance GUID:

```powershell
$env:WORKERDOCK_INSTANCE_ID='<stable-central-guid>'
$env:WORKERDOCK_SQL='<central-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply central 0001_registry
```

Add the existing central application database principal to the role:

```powershell
sqlcmd -S '<SQL_SERVER>' -d WorkerDockCentral -E -b `
  -v TenantRuntimePrincipal="" CentralRuntimePrincipal="<CENTER_DB_USER>" `
  -i .\deploy\permissions.sql
```

Configure the central application with the same GUID and its restricted runtime connection string. `WorkerDockCentralRuntime` grants only the central database access defined by the migration. The central identity must not have access to tenant databases.

## Roll back a database

Take and verify a backup first. Rollbacks run one migration at a time and are guarded against discarding populated state. Stop all web, scheduler, worker, and publisher processes before rollback. For a truly empty disposable tenant database, remove the runtime membership, then run:

```powershell
$env:WORKERDOCK_INSTANCE_ID='<same-tenant-guid>'
$env:WORKERDOCK_SQL='<tenant-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0008_retention
```

Continue with preceding IDs only when their down scripts' empty-state requirements are satisfied. For a populated database, restore a backup or perform a separately planned data migration; do not bypass a rollback guard. To remove the central registry, its rollback refuses registered tenants or audit data:

```powershell
$env:WORKERDOCK_INSTANCE_ID='<same-central-guid>'
$env:WORKERDOCK_SQL='<central-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback central 0001_registry
```

## SQL integration tests

Only run SQL integration tests against an isolated, disposable SQL Server instance. The `WORKERDOCK_TEST_SQL` identity needs permission to create and drop databases in `master`; fixtures generate `WorkerDock_Test_<32 hex digits>`, set compatibility 160, and drop only that generated database. Quartz process tests additionally require a distinct restricted SQL login in `WORKERDOCK_TEST_SQL_RUNTIME`. SQL target-template tests use a third distinct restricted login in `WORKERDOCK_TEST_SQL_TARGET_RUNTIME`. All logins must point to the same disposable server. Never use a production server or a database that contains data to preserve.

```powershell
$env:WORKERDOCK_TEST_SQL='<disposable-server-provisioner-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-restricted-runtime-login>'
$env:WORKERDOCK_TEST_SQL_TARGET_RUNTIME='<same-server-restricted-target-login>'
dotnet test tests/WorkerDock.IntegrationTests -c Release
```

The fixtures own their generated databases and test objects. Their test-only SQL lives under `tests/sql` and `tests/WorkerDock.IntegrationTests/Sql`; it is not part of runtime provisioning. Missing SQL configuration causes the SQL fixtures to report `SQL_TEST_BLOCKED`. Record actual runs in the [release evidence](../validation/release-evidence.md).

See [database object reference](../reference/database-objects.md), [Control Center setup](../control-center.md), and [Quartz integration](../quartz-integration.md) for object inventories and design details.
