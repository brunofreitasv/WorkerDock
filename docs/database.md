# Tenant database setup and verification

Use SQL Server 2022 or newer. The tenant database requires compatibility level 160. Set up a dedicated, disposable SQL instance for integration tests. The test login must be able to create and drop its own `WorkerDock_Test_<32 hex digits>` databases in `master`. `SqlDatabaseFixture` generates that name, creates the database, tests with `READ_COMMITTED_SNAPSHOT` both OFF and ON, and drops only that generated database. Never point the fixture at an existing user database.

On Windows, install a local SQL Server 2022 Developer or Express instance, start its Database Engine service, and have an administrator provision a dedicated test login with permission to create databases. Use a connection string such as `Server=localhost;Integrated Security=true;TrustServerCertificate=true;Encrypt=true` when that Windows identity is authorized. If Windows authentication is rejected, use a dedicated SQL test login provisioned by the administrator; do not change server permissions as part of the test run.

For tests, set `WORKERDOCK_TEST_SQL` to a connection string for that disposable instance. Do not include a database name that should be preserved: the fixture overrides Initial Catalog with `master` and its generated test database. Then run:

Quartz process tests also require `WORKERDOCK_TEST_SQL_RUNTIME`, a connection
string for a separate, pre-provisioned SQL-authentication login on the same
server. An administrator creates that server login on the disposable test
instance ahead of time; the fixture creates its database user, adds it to
`WorkerDockRuntime`, and installs/removes paired `quartz-probe.up.sql` /
`quartz-probe.down.sql` objects only inside its generated database. The
runtime login must not be the provisioner, a server administrator, or a
database owner. The fixture rejects a missing or reused login and verifies
`WorkerDockRuntime` membership plus absence of `CREATE TABLE` permission.
Store both connection strings in a local secret source or scoped environment;
do not put passwords in source, command arguments, or test logs. The runtime
string's `Initial Catalog` is replaced with the generated test database.
Both strings must use the same `Server` value.

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~MigrationTests|FullyQualifiedName~TenantSettingsTests'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzCapacityTests|FullyQualifiedName~QuartzSqlTopologyTests'
```

For local manual setup, create an empty tenant database at compatibility level 160. Supply a deployment-only migrator connection string in `WORKERDOCK_SQL` and a newly generated, stable GUID in `WORKERDOCK_INSTANCE_ID`; the GUID binds this database to one tenant context. Run `dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0002_quartz`. The migrator applies `0001_bootstrap` first, checks both script checksums before connecting, records them in `SchemaHistory`, and verifies the stored instance ID on later runs. Only the migrator login needs DDL rights. Add the runtime database user to `WorkerDockRuntime`, which grants settings/audit rights plus DML on both Quartz table sets. Configure `Tenant__ConnectionString` and `Tenant__InstanceId` for the web host from deployment secrets/configuration, never a browser selector.

Before any migration DDL, the runner reads the connected server's major version and the connected database's compatibility level. It requires SQL Server major version 16 or later and compatibility level exactly 160, returning `UnsupportedSqlServerVersion` or `UnsupportedCompatibilityLevel` otherwise. This check and the scripts still require live verification on the disposable SQL test instance.

Rollback requires a backup and an empty audit/outbox, default settings, and no runtime role members. Run `dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0001_bootstrap` only for an empty disposable database. Populated rollback returns `UnsafeRollback`; preserve the backup for restore instead. The migration uses a transaction and SQL application lock. `SchemaHistory` checksums are immutable once applied; add a new numbered pair to change a released schema.

For Quartz rollback, stop all scheduler, worker and publisher processes and
confirm both Quartz stores contain no work. Back up the database, then run
`dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0002_quartz`.
Its paired script refuses populated store tables and removes both table sets
transactionally. An idle process that could submit work must also be stopped;
the empty-table guard cannot prevent future writes by a process that remains
running.

The migration script is checked into `database/tenant` with its SHA-256 values in `manifest.json`. Central database migrations begin in Task 20. SQL integration outcomes must be reported separately from memory tests.


## Same application ports with real SQL

The memory instance and SQL deployment both use `ITenantSettingsService` and the same `/api/session`, `/api/settings` and antiforgery/policy routes. The React build is the same. No Task02 schema change or grant change is required. Do not reuse the disposable memory GUID for an existing tenant database: use that database's provisioned instance ID.

For a newly provisioned, dedicated empty local tenant database, run these exact steps with values supplied through deployment configuration; keep credentials out of source and shell history:

```powershell
# The dedicated database and migrator/runtime identities must already be provisioned.
$env:WORKERDOCK_INSTANCE_ID='<new-stable-instance-guid>'
$env:WORKERDOCK_SQL='<dedicated-local-tenant-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0001_bootstrap
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0002_quartz
$env:Tenant__Adapter='SqlServer'
$env:Tenant__InstanceId=$env:WORKERDOCK_INSTANCE_ID
$env:Tenant__ConnectionString='<dedicated-local-tenant-runtime-connection-string>'
$env:ASPNETCORE_ENVIRONMENT='Development'
npm.cmd --prefix frontend ci
npm.cmd --prefix frontend run build
dotnet dev-certs https --trust
dotnet run --project src/WorkerDock.Web --urls https://localhost:5001
```

In a separate test shell, only against the disposable test SQL instance provisioned above:

```powershell
$env:WORKERDOCK_TEST_SQL='<dedicated-disposable-sql-instance-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-distinct-restricted-sql-login-connection-string>'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~WorkerDock.IntegrationTests.MigrationTests|FullyQualifiedName~WorkerDock.IntegrationTests.TenantSettingsTests'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzCapacityTests|FullyQualifiedName~QuartzSqlTopologyTests'
```

For the independent in-memory instance and exact mock test commands, see [development.md](development.md). For Production/IIS, require HTTPS, the SQL runtime adapter and the protected data-protection key directory in [authentication.md](authentication.md). Configure the appropriate IIS hosting identity and directory ACLs during deployment, not through application database grants. Real SQL verification remains pending until an authorized disposable instance is supplied and those SQL tests pass. Memory tests do not prove durability, SQL locking/migrations, multi-process security isolation or Quartz clustering.

## Calendar persistence verification

After supplying the disposable `WORKERDOCK_TEST_SQL` connection as above, run:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~CalendarPersistenceTests&Store=Sql'
```

The seven restart fixtures create their own disposable database, apply both
numbered migrations, store a native trigger and versioned policy calendar via
`IScheduler`, shut down the first handle, open a new handle, and compare stored
filtered occurrences. These serializer/restart cases use the fixture provisioner;
the separate Quartz process suite above remains the required restricted-login and
cluster proof. No new schema objects, runtime grants or migration scripts are
introduced by the calendar payload. These SQL cases remain explicitly pending
until the configured instance runs them successfully. JSON and RAMJobStore passes
are recorded separately.

## Projection publication migration and SQL verification

After the committed execution migration, apply the ordered suffix migration
`0003a_projection` (the numbered `0004_templates` slot remains reserved). It adds
bounded LastErrorCode and ReviewAtUtc outbox metadata, a review index and only
SELECT on Attempts for WorkerDockRuntime. It grants no attempt mutations or DDL.
The manifest explicitly orders the paired migration and records both SHA-256
checksums. Stop runtime processes before rollback; rollback refuses populated
publication/review/error metadata. Never modify an already applied pair.

For the dedicated empty tenant database and identities provisioned above:

```powershell
$env:WORKERDOCK_INSTANCE_ID='<stable-dedicated-tenant-guid>'
$env:WORKERDOCK_SQL='<dedicated-tenant-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0003a_projection
$env:Tenant__InstanceId=$env:WORKERDOCK_INSTANCE_ID
$env:Tenant__ConnectionString='<same-tenant-restricted-runtime-connection-string>'
dotnet run --project src/WorkerDock.Scheduler -c Release
```

This starts only the control scheduler. Deploy Task08's Worker before enabling
execution acquisition; Task06 stores its exact future wrapper identity. Use the
runtime user with WorkerDockRuntime membership, never the migrator identity.
The Scheduler never creates or changes a database/schema itself.

Run against the dedicated disposable SQL Server 2022+ instance (compatibility
160) with its separately provisioned restricted SQL login:

```powershell
$env:WORKERDOCK_TEST_SQL='<disposable-test-server-owner-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-distinct-restricted-login-connection-string>'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~ProjectionSqlTests'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzCapacityTests|FullyQualifiedName~QuartzSqlTopologyTests'
```

Fixtures generate their own WorkerDock_Test databases, apply migrations, create
the runtime user within that database and remove their databases afterwards.
ProjectionSqlTests applies through 0003a_projection, including all predecessors,
and uses the runtime connection for projection and Quartz operations. Credentials
are supplied through environment configuration and never printed. With either
required SQL variable absent, the tests explicitly fail SQL_TEST_BLOCKED; SQL
publication/locking/migration and clustered restart guarantees remain pending.
After all services stop, only an empty disposable projection schema may use:

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0003a_projection
```
