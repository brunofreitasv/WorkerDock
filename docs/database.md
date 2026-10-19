# Tenant database setup and verification

Use SQL Server 2022 or newer. The tenant database requires compatibility level 160. Set up a dedicated, disposable SQL instance for integration tests. The test login must be able to create and drop its own `WorkerDock_Test_<32 hex digits>` databases in `master`. `SqlDatabaseFixture` generates that name, creates the database, tests with `READ_COMMITTED_SNAPSHOT` both OFF and ON, and drops only that generated database. Never point the fixture at an existing user database.

On Windows, install a local SQL Server 2022 Developer or Express instance, start its Database Engine service, and have an administrator provision a dedicated test login with permission to create databases. Use an integrated-authentication connection supplied through deployment secrets when that Windows identity is authorized. If Windows authentication is rejected, use a dedicated SQL test login provisioned by the administrator; do not change server permissions as part of the test run.

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

For local manual setup, create an empty tenant database at compatibility level 160. Supply a deployment-only migrator connection string in `WORKERDOCK_SQL` and a newly generated, stable GUID in `WORKERDOCK_INSTANCE_ID`; the GUID binds this database to one tenant context. Run `dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0004b_integration_settings`. The migrator applies all earlier manifest entries through `0004b_integration_settings`, starting with `0001_bootstrap`, checks both script checksums before connecting, records them in `SchemaHistory`, and verifies the stored instance ID on later runs. Only the migrator login needs DDL rights. Add the runtime database user to `WorkerDockRuntime`, which grants settings/audit rights plus DML on both Quartz table sets. Configure `Tenant__ConnectionString` and `Tenant__InstanceId` for the web host from deployment secrets/configuration, never a browser selector.

Before any migration DDL, the runner reads the connected server's major version and the connected database's compatibility level. It requires SQL Server major version 16 or later and compatibility level exactly 160, returning `UnsupportedSqlServerVersion` or `UnsupportedCompatibilityLevel` otherwise. This check and the scripts still require live verification on the disposable SQL test instance.

Rollback requires a backup and an empty audit/outbox, default settings, and no runtime role members. Run `dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0001_bootstrap` only for an empty disposable database. Populated rollback returns `UnsafeRollback`; preserve the backup for restore instead. The migration uses a transaction and SQL application lock. `SchemaHistory` checksums are immutable once applied; add a new numbered pair to change a released schema.

For Quartz rollback, stop all scheduler, worker and publisher processes and
confirm both Quartz stores contain no work. Back up the database, then run
`dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0002_quartz`.
Its paired script refuses populated store tables and removes both table sets
transactionally. An idle process that could submit work must also be stopped;
the empty-table guard cannot prevent future writes by a process that remains
running.

The migration script is checked into `database/tenant` with its SHA-256 values in `manifest.json`. The separate central registry migration and Control Center SQL setup are documented in [control-center.md](control-center.md), with paired scripts under `database/central`. SQL integration outcomes must be reported separately from memory tests.

## Tenant integration settings

The ordered `0004b_integration_settings` migration follows `0004_templates` and `0004a_profiles`. It creates the current vault row, the fixed `tenant-jobs` and `tenant-alerts` egress rows, and immutable configuration revision history. SQL writes, command receipts, and sanitized before/after revision audit rows share one transaction. `WorkerDockRuntime` receives only SELECT/INSERT/UPDATE rights and no DDL rights. The down migration refuses rollback after any vault or policy advances beyond its seed revision; restore a backup instead of discarding settings history.

Apply the migration on a fresh dedicated tenant database with:

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0004b_integration_settings
```

Set `Tenant__Adapter=SqlServer`, `Tenant__InstanceId` and `Tenant__ConnectionString` from protected deployment configuration. For local mock development, use `Tenant__Adapter=Memory` with `ASPNETCORE_ENVIRONMENT=Development`; this adapter does not establish SQL persistence or SQL target execution. SQL-backed migration, audit rollback, rowversion and restart checks require `WORKERDOCK_TEST_SQL` and remain `SQL_TEST_BLOCKED` until run against a disposable SQL Server 2022+ instance.


## Same application ports with real SQL

The memory instance and SQL deployment both use `ITenantSettingsService` and the same `/api/session`, `/api/settings` and antiforgery/policy routes. The React build is the same. Do not reuse the disposable memory GUID for an existing tenant database: use that database's provisioned instance ID.

For a newly provisioned, dedicated empty local tenant database, run these exact steps with values supplied through deployment configuration; keep credentials out of source and shell history:

```powershell
# The dedicated database and migrator/runtime identities must already be provisioned.
$env:WORKERDOCK_INSTANCE_ID='<new-stable-instance-guid>'
$env:WORKERDOCK_SQL='<dedicated-local-tenant-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0001_bootstrap
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0004_templates
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

This starts only the control scheduler. Deploy the Worker and complete its
capability/policy registration before enabling execution acquisition. Use the
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

## Attempt ownership migration and SQL proof

Apply `0003b_attempts` after `0003a_projection`; the manifest orders every
predecessor automatically. The `0004_templates` slot remains reserved. This
paired migration adds durable StopRequested/ForceStop markers, attempt exit/effect
certainty, and indexed minimal AttemptStatusEvents. It grants the runtime role
attempt mutation, permit release, worker/capability/policy reads, and status-event
append/read rights, without schema rights or direct Quartz changes. Rollback
refuses any attempts/status events or persisted stop markers. Stop every runtime
process first; populated histories require backup/restore.

For the previously provisioned dedicated tenant database and restricted runtime
identity, from the repository root:

```powershell
$env:WORKERDOCK_INSTANCE_ID='<stable-dedicated-tenant-guid>'
$env:WORKERDOCK_SQL='<dedicated-tenant-migrator-connection-string>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0003b_attempts
dotnet build WorkerDock.slnx -c Release
$env:Tenant__InstanceId=$env:WORKERDOCK_INSTANCE_ID
$env:Tenant__ConnectionString='<same-tenant-restricted-runtime-connection-string>'
$env:Executor__Path=(Resolve-Path 'src/WorkerDock.Executor/bin/Release/net10.0-windows/WorkerDock.Executor.exe').Path
$env:Worker__HostCeiling='10'
$env:Worker__DrainSeconds='40'
dotnet run --project src/WorkerDock.Worker -c Release
```

Start `dotnet run --project src/WorkerDock.Scheduler -c Release` in another shell
with the same tenant runtime configuration. The Scheduler starts only short
control/recovery/projection jobs; the Worker starts only execution acquisition.
Task08 deliberately fails admission closed until Task09 registers the current
boot/capabilities and opens effective policy gates. The Executor currently has no
production template factories; trusted built-in package composition arrives in
Task10. Do not bypass those gates to make an incomplete installation run work.
Executor path/arguments/catalog digests are administrator deployment configuration,
never job-supplied code. Task08 adds the foreground Worker composition root;
Windows service packaging remains part of deployment work.

Run the real fixtures only against the authorized disposable SQL instance:

```powershell
$env:WORKERDOCK_TEST_SQL='<disposable-test-server-owner-connection-string>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-distinct-restricted-login-connection-string>'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~AttemptSqlTests
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter FullyQualifiedName~ProjectionSqlTests
```

AttemptSqlTests applies through 0003b_attempts, uses the restricted runtime login,
and checks competing boots, SQL-time expiry, stale generation/boot rejection,
stop-before-success, concurrent expiry reviewers, safe descriptor identity and
paired migration round trip under both supported snapshot settings. These SQL
proofs remain pending while local SQL login/configuration is unavailable; the
fixtures explicitly fail `SQL_TEST_BLOCKED` without both SQL variables. The full
unfiltered solution suite retains those failures instead of silently skipping them.

Only an empty disposable attempt schema may use this rollback:

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0003b_attempts
```


## Task 09 live capacity setup and SQL proofs

Use the already provisioned empty dedicated tenant database and deployment-only
migrator identity described above. Supply values through local deployment
configuration; the placeholders below are not credentials to commit:

```powershell
$env:WORKERDOCK_SQL='<dedicated-tenant-migrator-connection>'
$env:WORKERDOCK_INSTANCE_ID='<stable-tenant-instance-guid>'
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0004_templates

$env:Tenant__ConnectionString='<restricted-tenant-runtime-connection>'
$env:Tenant__InstanceId=$env:WORKERDOCK_INSTANCE_ID
$env:Worker__InstallationId='<stable-guid-unique-to-this-installed-service>'
$env:Worker__CatalogDigest='<uppercase-installed-common-catalog-sha256>'
$env:Worker__HostCeiling='10'
$env:Executor__Path='<installed-production-executor-executable-path>'
# Supply Executor:Arguments and Executor:MetadataDigests from installation configuration.
dotnet run --project src/WorkerDock.Worker -c Release
```

The catalog digest algorithm and admission protocol are specified in
quartz-integration.md. Repeat worker startup with three distinct installation
IDs in separate service processes sharing the same tenant database and installed
catalog. The host ceiling is installation configuration; live limits within it
use the protected management routes without restarting. All initial template
limits are zero. Start the Scheduler and Web host using their existing deployment
instructions, then set desired template limits through the management API.
Built-in SQL/HTTP handlers arrive in Stage 03; the current stage's contained
execution demonstrations use test-only handlers from the test projects.

The suffix pair adds durable worker acknowledgement/readiness, policy catalog
identity, command policy revisions and a per-job admission sequence. It grants
only the DML needed on existing policy/worker/permit tables. The down script
refuses used policy/worker/permit state and restores the preceding schema. Task 09A subsequently rewrites the unpublished fresh-install pairs for the custom schema.

Run these exact SQL checks against the separate disposable test SQL instance,
with its pre-provisioned distinct restricted SQL login:

```powershell
$env:WORKERDOCK_TEST_SQL='<disposable-SQL-Server-2022+-provisioner-connection>'
$env:WORKERDOCK_TEST_SQL_RUNTIME='<same-server-distinct-restricted-runtime-login>'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~CapacityPolicySqlTests|FullyQualifiedName~AttemptSqlTests|FullyQualifiedName~ProjectionSqlTests'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~QuartzCapacityTests|FullyQualifiedName~QuartzSqlTopologyTests'
dotnet test WorkerDock.slnx -c Release
```

The new adapter fixture checks epoch rejection before and after reopening, no
attempt consumption for an old acquired delivery, deterministic reissue, and one
exclusive permit across queued work under both RCSI settings. Existing
QuartzCapacityTests use three probe processes to establish Quartz reservation
capacity. A real three-service Task 09 demonstration must additionally exercise
the production policy coordinator/SQL attempt adapter while pausing node
acknowledgements, lowering 3-to-1-to-0-to-2 with active attempts, rejoining an
old boot, and retaining retry/review permits. That full live-policy SQL proof is
still pending; probe and memory passes cannot substitute for it.

SQL_TEST_BLOCKED: WORKERDOCK_TEST_SQL is absent. Real custom-schema migration/rollback, grants, SQL durability and clustering proofs remain pending; memory and source-contract checks cannot establish them.

## Custom schema fresh installation (Task 09A)

Every WorkerDock-owned SQL object is in `[workerdock]`. The bootstrap migration
creates it before any table and drops it only after dependent migrations and
objects have been removed. The schema owner `[dbo]` is a database principal;
`sys.*` and `sys.sp_getapplock` remain SQL Server system objects. Runtime grants
remain per object, with no schema-wide DDL permission or runtime auto-provisioning.
Both Quartz stores are preinstalled by `0002_quartz`, and all scheduler, worker
and publisher paths use the shared `QuartzTopology` configuration with
`TablePrefix = "[workerdock].WDS_QRTZ_"` or `"[workerdock].WDE_QRTZ_"` and
`SchemaProvisioning.Validate`. Quartz 4.5.1 exposes these through the public
`AdoJobStoreOptions` / `ConfigureStore` API; see the
[Quartz configuration reference](https://www.quartz-scheduler.net/documentation/quartz-4.x/configuration/reference.html#persistent-job-store).
The attributed upstream SQL snapshot remains unmodified; only WorkerDock's
executable copies are transformed.

This is an authorized pre-release rewrite for fresh installation. Existing
unpublished prototype `dbo` databases are not automatically converted. Use a new
empty database or a separately reviewed data-move upgrade preserving data and
history. Do not apply rewritten checksums as an in-place upgrade to an applied
prototype or released database. Future tenant and central database migrations
must create/use their own `[workerdock]` namespace.

After installing SQL Server 2022 Developer/Express and starting its Database
Engine service, an authorized administrator can create a dedicated empty local
database using `sqlcmd` (install SQL Server command-line utilities if needed):

```powershell
sqlcmd -S localhost -E -C -b -Q "CREATE DATABASE [WorkerDock_Local]; ALTER DATABASE [WorkerDock_Local] SET COMPATIBILITY_LEVEL = 160;"
$env:WORKERDOCK_SQL='<deployment-only-connection-for-WorkerDock_Local>'
$env:WORKERDOCK_INSTANCE_ID=[guid]::NewGuid().ToString()
# Persist this instance ID in deployment configuration before starting hosts.
dotnet run --project src/WorkerDock.DatabaseMigrator -c Release -- apply tenant 0004_templates
# The distinct restricted server login WorkerDockRuntimeLocal must be provisioned by the administrator first.
sqlcmd -S localhost -E -C -b -d WorkerDock_Local -Q "CREATE USER [WorkerDockRuntimeLocal] FOR LOGIN [WorkerDockRuntimeLocal]; ALTER ROLE [WorkerDockRuntime] ADD MEMBER [WorkerDockRuntimeLocal];"
```

Use the restricted login's connection from deployment secrets for runtime hosts,
and the stored instance ID for `Tenant__InstanceId`. Only the migrator has DDL
rights. The disposable integration fixture separately needs the provisioner to
create/drop generated databases; do not use `WorkerDock_Local` as fixture data.
With `WORKERDOCK_TEST_SQL` and `WORKERDOCK_TEST_SQL_RUNTIME` supplied securely as
above, run the entire real SQL suite, including all-pairs up/down/up verification:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store=Sql'
dotnet test WorkerDock.slnx -c Release
dotnet build WorkerDock.slnx -c Release
```

For an unused disposable database, stop every host and remove its runtime role
membership before rollback. After backup and the documented empty-data checks,
reverse the pairs explicitly; the runner rolls back only the named pair:

```powershell
'0003d_dispatch','0003c_capacity','0003b_attempts','0003a_projection','0003_execution','0002_quartz','0001_bootstrap' | ForEach-Object {
    dotnet run --project src/WorkerDock.DatabaseMigrator -c Release -- rollback tenant $_
    if ($LASTEXITCODE -ne 0) { throw 'Rollback failed; inspect the safe error code before continuing.' }
}
```

SQL_TEST_BLOCKED remains the expected result while `WORKERDOCK_TEST_SQL` is absent.

## Stage 02 dispatch follow-up migration and SQL parity tests

After supplying the deployment connection and stable instance identity as above, apply the complete schema:

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -c Release -- apply tenant 0004_templates
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~CommandOutcomeSqlTests|FullyQualifiedName~ProjectionSqlTests|FullyQualifiedName~AttemptSqlTests|FullyQualifiedName~MigrationTests.Fresh_custom_schema'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'Store=Sql'
```

The paired `0003d_dispatch` migration adds `[workerdock].ProjectionMaintenance`, its per-object runtime grants, a filtered eligible-execution index, and a pending-command target index. Stop all hosts before rollback; reverse `0003d_dispatch` before `0003c_capacity`. The cursor is maintenance progress only and never authorizes an attempt or replaces Quartz acquisition. SQL parity, rollback and large-dataset timing tests require real SQL Server with RCSI OFF and ON; memory/source tests are not SQL execution proof.

## Task 10 immutable template catalog

Stop/drain the worker pool and back up the tenant database, then run the paired migration with the already provisioned migration identity:

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0004_templates
```

`0004_templates` follows `0003d_dispatch`. It adds nullable descriptor JSON and retirement state to `[workerdock].TemplateCatalog`, its JSON/retirement checks and `[workerdock].TemplateCatalog_Immutable`. The existing WorkerDockRuntime role retains SELECT and receives only INSERT and column-level UPDATE on descriptor/digest/enablement/retirement; it receives no DELETE or DDL. All metadata publication runs through the trusted catalog adapter and existing tenant transaction lock. Initial promotion is limited to the exact two seeded built-in 1.0.0 placeholder digests with NULL metadata. Published descriptors/digests cannot be updated/deleted, and retired versions cannot be reenabled through registration. Profile and tenant vault/egress settings tables are owned by later tasks, not this migration.

For an unpopulated disposable catalog, stop every runtime writer before rollback. The paired down script refuses published descriptors, retired versions or later migrations; a populated deployment requires backup restore or a reviewed forward change.

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0004_templates
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0004_templates
```

With the separately provisioned disposable `WORKERDOCK_TEST_SQL` and distinct restricted `WORKERDOCK_TEST_SQL_RUNTIME` login described above, run:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~TemplateCatalogSqlTests&Store=Sql'
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~MigrationTests.Fresh_custom_schema_installs_and_rolls_back_all_pairs&Store=Sql'
```

These cases exercise snapshot OFF/ON, exact bootstrap promotion with restricted grants, immutable mismatch rejection, retirement without metadata loss, rollback refusal and empty up/down/up. Fresh installation now applies all eight pairs through `0004_templates`. The source checksum/custom-schema tests and memory catalog tests are SQL-independent; they cannot prove migration execution, trigger behavior, runtime grants, locking or restart durability. Live SQL acceptance is **SQL_TEST_BLOCKED** until the authorized test credentials are available and the above commands pass.

## Task 14 durable scheduling migration

`0005_scheduling` follows `0004b_integration_settings` and adds the durable schedule cursor, recovery ranges, job activation boundaries, and occurrence foreign-key/check constraints. It grants only the required schedule and recovery-event DML to `WorkerDockRuntime`; all schema changes remain migration-only. Stop/drain writers and back up before applying with the separate migration identity:

```powershell
dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0005_scheduling
```

Rollback refuses to discard any saved schedule, scheduled execution, or recovery event. Remove this feature's business data only through an explicitly reviewed migration procedure before rollback. Verify the apply/rollback round trip on the disposable SQL instance; until that has run, real SQL acceptance remains `SQL_TEST_BLOCKED`.
