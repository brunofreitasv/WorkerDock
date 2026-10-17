# Tenant database setup and verification

Use SQL Server 2022 or newer. The tenant database requires compatibility level 160. Set up a dedicated, disposable SQL instance for integration tests. The test login must be able to create and drop its own `WorkerDock_Test_<32 hex digits>` databases in `master`. `SqlDatabaseFixture` generates that name, creates the database, tests with `READ_COMMITTED_SNAPSHOT` both OFF and ON, and drops only that generated database. Never point the fixture at an existing user database.

On Windows, install a local SQL Server 2022 Developer or Express instance, start its Database Engine service, and have an administrator provision a dedicated test login with permission to create databases. Use a connection string such as `Server=localhost;Integrated Security=true;TrustServerCertificate=true;Encrypt=true` when that Windows identity is authorized. If Windows authentication is rejected, use a dedicated SQL test login provisioned by the administrator; do not change server permissions as part of the test run.

For tests, set `WORKERDOCK_TEST_SQL` to a connection string for that disposable instance. Do not include a database name that should be preserved: the fixture overrides Initial Catalog with `master` and its generated test database. Then run:

```powershell
dotnet test tests/WorkerDock.IntegrationTests -c Release --filter 'FullyQualifiedName~MigrationTests|FullyQualifiedName~TenantSettingsTests'
```

For local manual setup, create an empty tenant database at compatibility level 160. Supply a deployment-only migrator connection string in `WORKERDOCK_SQL` and a newly generated, stable GUID in `WORKERDOCK_INSTANCE_ID`; the GUID binds this database to one tenant context. Run `dotnet run --project src/WorkerDock.DatabaseMigrator -- apply tenant 0001_bootstrap`. The migrator checks both script checksums before connecting, records them in `SchemaHistory`, and verifies the stored instance ID on later runs. Only the migrator login needs DDL rights. Add the runtime database user to `WorkerDockRuntime`, which grants settings read/update and audit append rights. Configure `Tenant__ConnectionString` and `Tenant__InstanceId` for the web host from deployment secrets/configuration, never a browser selector.

Before any migration DDL, the runner reads the connected server's major version and the connected database's compatibility level. It requires SQL Server major version 16 or later and compatibility level exactly 160, returning `UnsupportedSqlServerVersion` or `UnsupportedCompatibilityLevel` otherwise. This check and the scripts still require live verification on the disposable SQL test instance.

Rollback requires a backup and an empty audit/outbox, default settings, and no runtime role members. Run `dotnet run --project src/WorkerDock.DatabaseMigrator -- rollback tenant 0001_bootstrap` only for an empty disposable database. Populated rollback returns `UnsafeRollback`; preserve the backup for restore instead. The migration uses a transaction and SQL application lock. `SchemaHistory` checksums are immutable once applied; add a new numbered pair to change a released schema.

The migration script is checked into `database/tenant` with its SHA-256 values in `manifest.json`. Central database migrations begin in Task 20. SQL integration outcomes must be reported separately from memory tests.
